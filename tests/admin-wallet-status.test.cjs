const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// Exercise the shipped script with a small DOM and clock, without opening a live
// wallet or sending credentials. Only the page's startup request is suppressed.
const source = fs.readFileSync(path.join(__dirname, '../src/YemenDrive.Api/wwwroot/admin.js'), 'utf8');
const settle = () => new Promise(resolve => setImmediate(resolve));
const deferred = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};
const method = (id, kind = 0) => ({ id, kind, nameAr: `Wallet ${id}`, providerCode: 'Jaib', isActive: true });
const login = status => ({ status, hasCredentials: true, isEnabled: true, providerCode: 'Jaib' });

function harness(request, items = [method(1)]) {
  let now = 0;
  let timerId = 0;
  const timers = new Map();
  const nodes = new Map();
  const cells = new Map();
  const documentEvents = new Map();
  const windowEvents = new Map();
  const addListener = (events, name, callback) => {
    if (!events.has(name)) events.set(name, []);
    events.get(name).push(callback);
  };
  class Node {
    constructor() {
      this._html = ''; this.writes = 0; this.value = ''; this.dataset = {};
      this.events = new Map(); this.classList = { add() {}, remove() {}, toggle() {} };
      this.elements = { namedItem: () => null };
    }
    get innerHTML() { return this._html; }
    set innerHTML(html) {
      this._html = html; this.writes++;
      if (this === nodes.get('#payment-methods-body')) {
        cells.clear();
        for (const match of html.matchAll(/<td data-wallet-login-id="(\d+)">([\s\S]*?)<\/td>/g)) {
          const cell = new Node(); cell._html = match[2]; cells.set(Number(match[1]), cell);
        }
      }
    }
    addEventListener(name, callback) { addListener(this.events, name, callback); }
    closest() { return this; }
    focus() {}
    querySelectorAll() { return []; }
    appendChild() {}
  }
  function node(selector) {
    const id = selector.match(/data-wallet-login-id="(\d+)"/)?.[1];
    if (id) return cells.get(Number(id)) || null;
    if (!nodes.has(selector)) nodes.set(selector, new Node());
    return nodes.get(selector);
  }
  const storage = () => {
    const values = new Map();
    return { getItem: key => values.get(key) || null, setItem: (key, value) => values.set(key, value), removeItem: key => values.delete(key) };
  };
  const document = {
    hidden: false, querySelector: node, querySelectorAll: () => [],
    addEventListener: (name, callback) => addListener(documentEvents, name, callback),
    documentElement: new Node(), body: new Node()
  };
  const context = {
    console, AbortController, document, sessionStorage: storage(), localStorage: storage(),
    addEventListener: (name, callback) => addListener(windowEvents, name, callback),
    setTimeout: (callback, delay) => { const id = ++timerId; timers.set(id, { due: now + delay, callback }); return id; },
    clearTimeout: id => timers.delete(id), clearInterval: id => timers.delete(id),
  };
  context.window = context;
  vm.createContext(context);
  vm.runInContext(source.replace(/^start\(\);\r?$/m, ''), context, { filename: 'admin.js' });
  const state = vm.runInContext('state', context);
  state.view = 'payment-methods'; state.databaseConfigured = true; state.walletLoginStates = new Map();
  context.sessionStorage.setItem('yemendrive_admin_token', 'fixture-session');
  const h = { context, state, nodes, cells, timers, document, listItems: items, requests: [], listCalls: 0, node };
  context.execute = async () => { h.listCalls++; return { data: h.listItems.slice() }; };
  context.adminFetch = async (url, options) => {
    h.requests.push({ url, options });
    const data = await request(url, options);
    return { ok: true, json: async () => ({ success: true, data }) };
  };
  for (const name of ['bindActionTooltips', 'initializePageScrollJump', 'initializeFinancialReportDates',
    'initializeFinancialOperationDate', 'initializeFinancialOperationFilterDate', 'stopAdminRealtime', 'loadDashboard'])
    context[name] = () => {};
  context.bindEvents();
  h.emitDocument = async name => { for (const cb of documentEvents.get(name) || []) cb({}); await settle(); };
  h.emitWindow = async name => { for (const cb of windowEvents.get(name) || []) cb({}); await settle(); };
  h.tick = async duration => {
    const target = now + duration;
    while (true) {
      const next = [...timers].filter(([, entry]) => entry.due <= target).sort((a, b) => a[1].due - b[1].due)[0];
      if (!next) break;
      now = next[1].due; timers.delete(next[0]); next[1].callback(); await settle();
    }
    now = target;
  };
  h.close = () => context.stopWalletLoginRefresh();
  return h;
}

test('status transitions reach the existing table automatically and only query wallet status', async t => {
  let phase = 'Pending';
  const h = harness(async () => login(phase), [method(1), method(2), method(3, 1)]);
  t.after(h.close);
  await h.context.loadPaymentMethods();
  const body = h.node('#payment-methods-body');
  const edit = h.node('#unrelated-edit'); edit.value = 'unsaved input';
  assert.match(h.cells.get(1).innerHTML, /بانتظار تسجيل الدخول/);
  phase = 'LoggingIn'; await h.tick(3000);
  assert.match(h.cells.get(1).innerHTML, /جارٍ تسجيل الدخول/);
  phase = 'Ready'; await h.tick(3000);
  assert.match(h.cells.get(1).innerHTML, /متصلة/);
  assert.equal(body.writes, 1, 'polling preserves the existing table and row actions');
  assert.equal(edit.value, 'unsaved input');
  assert.equal(h.listCalls, 1, 'polling does not reload the method list');
  assert.equal(h.requests.length, 6);
  assert.ok(h.requests.every(({ url, options }) => /^\/api\/admin\/payment-methods\/[12]\/wallet-login$/.test(url) && options.cache === 'no-store'));
});

test('unchanged statuses do not replace the cell DOM', async t => {
  const h = harness(async () => login('Ready')); t.after(h.close);
  await h.context.loadPaymentMethods();
  const cell = h.cells.get(1);
  const writes = cell.writes;
  await h.tick(9000);
  assert.equal(cell.writes, writes);
  assert.equal(h.cells.get(1), cell);
});

test('a slow status request never overlaps another polling batch', async t => {
  const waiting = deferred();
  const h = harness(() => waiting.promise); t.after(h.close);
  const loading = h.context.loadPaymentMethods(); await settle();
  await h.context.refreshWalletLoginStates();
  await h.tick(9000);
  assert.equal(h.requests.length, 1);
  waiting.resolve(login('Ready')); await loading;
  assert.match(h.cells.get(1).innerHTML, /متصلة/);
  await h.tick(3000);
  assert.equal(h.requests.length, 2);
});

test('hidden pages abort work, ignore late responses, and resume on visibility', async t => {
  const waiting = deferred(); let slow = true;
  const h = harness(() => slow ? waiting.promise : Promise.resolve(login('Ready'))); t.after(h.close);
  const loading = h.context.loadPaymentMethods(); await settle();
  h.document.hidden = true; await h.emitDocument('visibilitychange');
  assert.equal(h.requests[0].options.signal.aborted, true);
  waiting.resolve(login('Failed')); await loading;
  assert.doesNotMatch(h.cells.get(1).innerHTML, /تعذر تسجيل الدخول/);
  await h.tick(9000); assert.equal(h.requests.length, 1);
  slow = false; h.document.hidden = false; await h.emitDocument('visibilitychange');
  assert.match(h.cells.get(1).innerHTML, /متصلة/);
});

test('leaving the page or signing out prevents further updates', async t => {
  const h = harness(async () => login('Ready')); t.after(h.close);
  await h.context.loadPaymentMethods();
  await h.context.setView('overview');
  const count = h.requests.length;
  await h.tick(9000); await h.emitWindow('online');
  assert.equal(h.requests.length, count);
  await h.context.setView('payment-methods');
  assert.equal(h.requests.length, count + 1);
  h.context.showAdminLogin();
  await h.tick(9000); await h.emitWindow('pageshow');
  assert.equal(h.requests.length, count + 1);
  assert.equal(h.context.sessionStorage.getItem('yemendrive_admin_token'), null);
});

test('an old response cannot overwrite a newly loaded method list', async t => {
  const waiting = deferred(); let slow = true;
  const h = harness(() => slow ? waiting.promise : Promise.resolve(login('Ready'))); t.after(h.close);
  const oldLoad = h.context.loadPaymentMethods(); await settle();
  slow = false;
  await h.context.loadPaymentMethods();
  const current = h.cells.get(1);
  waiting.resolve(login('Failed')); await oldLoad;
  assert.equal(h.cells.get(1), current);
  assert.match(current.innerHTML, /متصلة/);
  assert.equal(h.state.walletLoginStates.get(1).status, 'Ready');
});

test('temporary request failure is shown and recovers on the next poll', async t => {
  let failing = true;
  const h = harness(async () => { if (failing) throw new Error('offline'); return login('Ready'); }); t.after(h.close);
  await h.context.loadPaymentMethods();
  assert.match(h.cells.get(1).innerHTML, /تعذر تحميل الحالة/);
  failing = false; await h.tick(3000);
  assert.match(h.cells.get(1).innerHTML, /متصلة/);
});

test('a hung network request times out and polling can recover', async t => {
  let hanging = true;
  const h = harness((url, { signal }) => hanging
    ? new Promise((resolve, reject) => signal.addEventListener('abort', () => reject(new Error('aborted')), { once: true }))
    : Promise.resolve(login('Ready'))); t.after(h.close);
  const loading = h.context.loadPaymentMethods(); await settle();
  await h.tick(15000); await loading;
  assert.equal(h.requests[0].options.signal.aborted, true);
  assert.match(h.cells.get(1).innerHTML, /تعذر تحميل الحالة/);
  hanging = false; await h.tick(3000);
  assert.match(h.cells.get(1).innerHTML, /متصلة/);
});

test('non-wallet lists and unconfigured pages do not poll', async t => {
  const h = harness(async () => login('Ready'), [method(3, 1)]); t.after(h.close);
  await h.context.loadPaymentMethods(); await h.tick(12000);
  assert.equal(h.requests.length, 0);
  h.state.paymentMethods = [method(1)]; h.state.databaseConfigured = false;
  await h.context.refreshWalletLoginStates(); await h.tick(12000);
  assert.equal(h.requests.length, 0);
});

test('a failed manual list reload still allows existing status cells to recover', async t => {
  let phase = 'Pending';
  const h = harness(async () => login(phase)); t.after(h.close);
  await h.context.loadPaymentMethods();
  h.context.execute = async () => { throw new Error('temporary list failure'); };
  await assert.rejects(h.context.loadPaymentMethods(), /temporary list failure/);
  phase = 'Ready'; await h.tick(3000);
  assert.match(h.cells.get(1).innerHTML, /متصلة/);
  assert.equal(h.node('#payment-methods-body').writes, 1);
});

test('returning to the visible wallet page after sign-in restarts status refresh', async t => {
  const h = harness(async () => login('Ready')); t.after(h.close);
  await h.context.loadPaymentMethods();
  h.context.showAdminLogin();
  const count = h.requests.length;
  h.context.sessionStorage.setItem('yemendrive_admin_token', 'new-fixture-session');
  h.context.showAdminApp(); await settle();
  assert.equal(h.requests.length, count + 1);
  await h.tick(3000);
  assert.equal(h.requests.length, count + 2);
});
