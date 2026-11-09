const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.join(__dirname, '../src/YemenDrive.Api/wwwroot');
const source = fs.readFileSync(path.join(root, 'admin.js'), 'utf8');
const syncSource = fs.readFileSync(path.join(root, 'admin-state-sync.js'), 'utf8');

function harness(signalR) {
  const nodes = new Map(), documentEvents = new Map(), requests = [], timers = new Map();
  let timerId = 0;
  class Node {
    constructor() {
      this.value = ''; this.dataset = {}; this.textContent = ''; this.innerHTML = ''; this.open = false;
      const classes = new Set();
      this.classList = { add: (...names) => names.forEach(n => classes.add(n)), remove: (...names) => names.forEach(n => classes.delete(n)),
        contains: name => classes.has(name), toggle: (name, enabled) => enabled ? classes.add(name) : classes.delete(name) };
      this.elements = { namedItem: () => null };
    }
    addEventListener() {}
    querySelectorAll() { return []; }
    append() {}
    appendChild() {}
    prepend() {}
    remove() {}
    focus() {}
    showModal() { this.open = true; }
    close() { this.open = false; }
  }
  const node = selector => {
    if (selector.startsWith('#modal-fields [name=')) return null;
    if (!nodes.has(selector)) nodes.set(selector, new Node());
    return nodes.get(selector);
  };
  const storage = () => {
    const values = new Map();
    return { getItem: key => values.get(key) || null, setItem: (key, value) => values.set(key, value), removeItem: key => values.delete(key) };
  };
  const context = {
    console, AbortController, document: { hidden: false, querySelector: node, querySelectorAll: () => [], createElement: () => new Node(),
      documentElement: new Node(), body: new Node(), addEventListener: (name, callback) => documentEvents.set(name, callback) },
    sessionStorage: storage(), localStorage: storage(), addEventListener() {}, confirm: () => true,
    setTimeout: callback => { timers.set(++timerId, callback); return timerId; },
    setInterval: callback => { timers.set(++timerId, callback); return timerId; },
    clearTimeout: id => timers.delete(id), clearInterval: id => timers.delete(id)
  };
  context.window = context;
  if (signalR !== undefined) context.signalR = signalR;
  vm.createContext(context);
  vm.runInContext(syncSource, context, { filename: 'admin-state-sync.js' });
  vm.runInContext(source.replace(/^start\(\);\r?$/m, ''), context, { filename: 'admin.js' });
  vm.runInContext("state.databaseConfigured = true; administratorPermissions = new Set(['*']);", context);
  context.sessionStorage.setItem('yemendrive_admin_token', 'test-session');
  context.adminFetch = async (url, options) => {
    const payload = JSON.parse(options.body);
    requests.push({ url, ...payload });
    return { status: 200, json: async () => ({ success: true, message: 'saved', data: payload.operation === 'add' ? { id: 1 } : [] }) };
  };
  for (const name of ['bindActionTooltips', 'initializePageScrollJump', 'initializeFinancialReportDates',
    'initializeFinancialOperationDate', 'initializeFinancialOperationFilterDate', 'applySegmentedFieldDesign']) context[name] = () => {};
  return { context, node, documentEvents, requests, timers, read: code => vm.runInContext(code, context) };
}

test('reading administrator permissions does not ask to confirm a write', async () => {
  const h = harness();
  let confirmations = 0;
  h.context.confirm = () => { confirmations++; return false; };
  await h.context.execute('AdministratorPermissionModel', 'mine', {});
  assert.equal(confirmations, 0);
  assert.equal(h.requests[0].operation, 'mine');
  await assert.rejects(h.context.execute('AdministratorPermissionModel', 'save', {}));
  assert.equal(confirmations, 1);
  assert.equal(h.requests.length, 1);
});

test('navigation continues to canonical REST reads when the SignalR script is missing', async () => {
  const h = harness();
  assert.equal('signalR' in h.context, false);
  await h.context.startAdminRealtime();
  await h.context.setView('users');
  await h.context.setView('areas');
  assert.deepEqual(h.requests.map(r => [r.url, r.model, r.operation]), [
    ['/api/admin/execute', 'UserModel', 'list'], ['/api/admin/execute', 'ServiceAreaModel', 'list']
  ]);
  assert.equal(h.node('#page-title').textContent, 'مناطق الخدمة');
  assert.equal(h.node('#live-rides-connection').classList.contains('connected'), false);
  assert.match(h.node('#live-rides-connection').textContent, /غير متاحة|غير متصل/);
});

test('forms still open, submit over REST, and refresh their page without SignalR', async () => {
  const h = harness();
  await h.context.setView('areas');
  await h.context.openForm('serviceArea');
  assert.equal(h.node('#form-modal').open, true);
  const fields = new Map([['countryNameAr', { value: 'اليمن' }], ['countryCode', { value: 'YE' }],
    ['cityNameAr', { value: 'صنعاء' }], ['isActive', { checked: true }]]);
  const form = { elements: { namedItem: name => fields.get(name) || null } };
  await h.context.submitEntityForm({ preventDefault() {}, currentTarget: form });
  const write = h.requests.find(r => r.operation === 'add');
  assert.equal(write.model, 'ServiceAreaModel');
  assert.equal(write.data.cityNameAr, 'صنعاء');
  assert.equal(write.data.isActive, true);
  assert.equal(h.node('#modal-result').className, 'inline-result success');
  assert.equal(h.node('#modal-submit').disabled, false);
  assert.equal(h.requests.filter(r => r.model === 'ServiceAreaModel' && r.operation === 'list').length, 2);
});

test('visibility, state disconnection and logout do not require the optional SDK', () => {
  const h = harness();
  h.context.bindEvents();
  assert.doesNotThrow(() => h.documentEvents.get('visibilitychange')());
  assert.doesNotThrow(() => h.read('adminStateSync.disconnect()'));
  assert.doesNotThrow(() => h.context.showAdminLogin());
  assert.equal(h.context.sessionStorage.getItem('yemendrive_admin_token'), null);
  assert.equal(h.node('#live-rides-connection').textContent, 'غير متصل');
  assert.equal(h.node('#live-rides-connection').classList.contains('connected'), false);
});

test('a partially loaded or failed SDK builder cannot abort REST startup', async () => {
  for (const sdk of [{}, { HubConnectionState: { Connected: 'Connected' }, LogLevel: { Warning: 3 },
    HubConnectionBuilder: class { constructor() { throw new Error('SDK initialization failed'); } } }]) {
    const h = harness(sdk);
    await assert.doesNotReject(h.context.startAdminRealtime());
    await h.context.setView('users');
    assert.equal(h.requests[0].model, 'UserModel');
    assert.equal(h.node('#live-rides-connection').classList.contains('connected'), false);
    assert.match(h.node('#live-rides-connection').textContent, /غير متاحة|تعذر/);
  }
});

function fakeSignalR(failStart = false) {
  const calls = [], handlers = new Map();
  const connection = {
    state: 'Disconnected', on: (name, fn) => handlers.set(name, fn),
    onreconnecting: fn => handlers.set('reconnecting', fn), onreconnected: fn => handlers.set('reconnected', fn), onclose: fn => handlers.set('close', fn),
    async start() { calls.push('start'); if (failStart) throw Error('network down'); this.state = 'Connected'; },
    async stop() { calls.push('stop'); this.state = 'Disconnected'; },
    async invoke(name, ...args) { calls.push([name, ...args]); return { enabled: true }; }
  };
  class HubConnectionBuilder {
    withUrl() { return this; } withAutomaticReconnect() { return this; } configureLogging() { return this; } build() { return connection; }
  }
  return { sdk: { HubConnectionBuilder, HubConnectionState: { Connected: 'Connected' }, LogLevel: { Warning: 3 } }, connection, calls, handlers };
}

test('connection failure stays visibly disconnected and allows manual REST navigation', async () => {
  const fake = fakeSignalR(true), h = harness(fake.sdk);
  await h.context.startAdminRealtime();
  await h.context.setView('users');
  assert.equal(h.requests[0].model, 'UserModel');
  assert.equal(h.node('#live-rides-connection').textContent, 'تعذر الاتصال المباشر');
  assert.equal(h.node('#live-rides-connection').classList.contains('connected'), false);
  assert.equal(h.read('adminHubConnection'), null);
});

test('available SignalR still connects, watches maps, recovers and disconnects', async () => {
  const fake = fakeSignalR(), h = harness(fake.sdk);
  let recoveries = 0;
  h.context.recoverAdminState = async () => { recoveries++; h.context.setLiveConnectionStatus('متصل — الحالة محدثة', true); };
  await h.context.startAdminRealtime();
  assert.deepEqual(fake.calls.slice(0, 2), ['start', ['JoinOperations']]);
  assert.ok(fake.handlers.has('driverLocationUpdated'));
  assert.ok(fake.handlers.has('stateChanged'));
  assert.equal(h.node('#live-rides-connection').classList.contains('connected'), true);
  h.read("state.view = 'rides'; state.liveRides.set(42, { id: 42 });");
  await h.context.syncAdminMapWatches();
  assert.ok(fake.calls.some(call => Array.isArray(call) && call[0] === 'WatchRideMap' && call[1] === 42));
  fake.connection.state = 'Reconnecting'; fake.handlers.get('reconnecting')();
  assert.equal(h.node('#live-rides-connection').classList.contains('connected'), false);
  fake.connection.state = 'Connected'; await fake.handlers.get('reconnected')();
  assert.equal(recoveries, 2);
  h.context.stopAdminRealtime();
  assert.equal(h.node('#live-rides-connection').textContent, 'غير متصل');
  assert.equal(fake.calls.at(-1), 'stop');
});
