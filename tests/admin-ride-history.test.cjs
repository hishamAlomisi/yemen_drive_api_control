const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function harness() {
  const nodes = new Map(), requests = [];
  const node = selector => {
    if (!nodes.has(selector)) {
      const classes = new Set();
      const item = { value: '', textContent: '', innerHTML: '', disabled: false, dataset: {},
        classList: { add: x => classes.add(x), remove: x => classes.delete(x), contains: x => classes.has(x), toggle: (x, enabled) => enabled ? classes.add(x) : classes.delete(x) },
        closest: () => item, elements: { namedItem: () => null }, focus() {} };
      nodes.set(selector, item);
    }
    return nodes.get(selector);
  };
  const storage = { getItem: () => null, removeItem() {}, setItem() {} };
  const context = { console, Intl, Date, document: { querySelector: node, querySelectorAll: () => [], addEventListener() {} },
    sessionStorage: storage, localStorage: storage, setTimeout: () => 1, clearTimeout() {}, setInterval: () => 1, clearInterval() {},
    AdminStateSync: class { disconnect() {} }, AbortController, addEventListener() {} };
  context.window = context;
  vm.createContext(context);
  const source = fs.readFileSync(path.join(__dirname, '../src/YemenDrive.Api/wwwroot/admin.js'), 'utf8');
  vm.runInContext(source.replace(/^start\(\);\r?$/m, ''), context);
  context.execute = async (model, operation, data) => { requests.push({ model, operation, data }); return { data: { rows: [], totalCount: 0, pageNumber: data.pageNumber, totalPages: 0 } }; };
  context.stopWalletLoginRefresh = () => {};
  context.stopAdminRealtime = () => {};
  return { context, node, requests, read: code => vm.runInContext(code, context) };
}
const ride104 = { id: 104, customerId: 15, customerName: 'راكب تجريبي', driverName: 'سائق تجريبي', status: 6,
  pickupLabel: 'الانطلاق', destinationLabel: 'الوجهة', customerPrice: 1840,
  createdAtUtc: '2026-11-05T16:32:19.5429934Z', acceptedAtUtc: '2026-11-05T16:32:56.2051355Z', serviceEndedAtUtc: '2026-11-05T16:36:53.8883648Z' };
function reply(rows, pageNumber = 1, totalCount = rows.length) { return { data: { rows, pageNumber, totalCount, pageSize: 100, totalPages: Math.ceil(totalCount / 100) } }; }

test('both tables identify completed104 and show agreement time in Yemen independently of browser timezone', async () => {
  const h = harness();
  h.context.execute = async () => reply([ride104], 1, 108);
  await h.context.loadRides(); await h.context.loadHistory();
  for (const selector of ['#rides-body', '#history-body']) {
    const html = h.node(selector).innerHTML;
    assert.match(html, /data-ride-row="104"/); assert.match(html, /#104/); assert.match(html, /مكتملة/);
    assert.match(html, /الطلب:/); assert.match(html, /الاتفاق:/); assert.match(html, /7:32:56/);
    assert.match(html, /انتهاء الخدمة:/);
  }
  assert.equal(h.context.rideTableDate('2026-11-05T16:32:56'), h.context.rideTableDate('2026-11-05T16:32:56Z'));
  assert.equal(h.context.rideTableDate('invalid'), '—');
});

test('Arabic exact number and completed filter are sent to the server with paging instead of searching only the first200', async () => {
  const h = harness(); h.node('#ride-number').value = '١٠٤'; h.node('#ride-filter').value = '6';
  await h.context.loadRides();
  assert.deepEqual(JSON.parse(JSON.stringify(h.requests[0])), { model: 'RideModel', operation: 'list', data: { includePaging: true, pageNumber: 1, pageSize: 100, status: 6, id: 104 } });
  h.node('#history-number').value = '۲۵۰'; await h.context.loadHistory();
  assert.equal(h.requests[1].data.id, 250);
});

test('history can load older pages and pager reflects exact server total', async () => {
  const h = harness();
  h.context.execute = async (_, __, data) => reply([{ ...ride104, id: data.pageNumber === 1 ? 207 : 7 }], data.pageNumber, 207);
  await h.context.loadHistory();
  assert.equal(h.node('#history-page-previous').disabled, true); assert.equal(h.node('#history-page-next').disabled, false);
  await h.context.loadHistory(3);
  assert.match(h.node('#history-body').innerHTML, /#7/); assert.match(h.node('#history-list-feedback').textContent, /207.*3.*3/);
  assert.equal(h.node('#history-page-previous').disabled, false); assert.equal(h.node('#history-page-next').disabled, true);
});

test('slower previous status request cannot replace the current completed104 result', async () => {
  const h = harness(); let resolveFirst;
  h.context.execute = (_, __, data) => data.status === 6 ? Promise.resolve(reply([ride104])) : new Promise(resolve => { resolveFirst = resolve; });
  const first = h.context.loadRides();
  h.node('#ride-filter').value = '6'; await h.context.loadRides();
  resolveFirst(reply([{ ...ride104, id: 99, status: 7 }])); await first;
  assert.match(h.node('#rides-body').innerHTML, /#104/); assert.doesNotMatch(h.node('#rides-body').innerHTML, /#99/);
});

test('failed refresh clears stale rows, exposes error and allows a successful retry', async () => {
  const h = harness(); h.context.execute = async () => reply([ride104]); await h.context.loadHistory();
  h.context.execute = async () => { throw new Error('الخادم غير متصل'); };
  await assert.rejects(h.context.loadHistory(), /غير متصل/);
  assert.equal(h.node('#history-body').innerHTML, ''); assert.equal(h.node('#history-list-refresh').disabled, false);
  assert.equal(h.node('#history-list-feedback').classList.contains('error'), true);
  h.context.execute = async () => reply([ride104]); await h.context.loadHistory();
  assert.match(h.node('#history-body').innerHTML, /#104/); assert.equal(h.node('#history-list-feedback').classList.contains('error'), false);
});

test('invalid number cancels a pending obsolete read and leaves refresh usable', async () => {
  const h = harness(); let resolve;
  h.context.execute = () => new Promise(r => { resolve = r; }); const pending = h.context.loadRides();
  h.node('#ride-number').value = '-1'; await h.context.loadRides();
  assert.equal(h.node('#ride-list-refresh').disabled, false); assert.match(h.node('#ride-list-feedback').textContent, /رقم رحلة صحيح/);
  resolve(reply([ride104])); await pending; assert.equal(h.node('#rides-body').innerHTML, '');
});

test('logout invalidates a pending history read and clears retained ride data', async () => {
  const h = harness(); let resolve;
  h.context.execute = () => new Promise(r => { resolve = r; }); const pending = h.context.loadHistory();
  h.context.showAdminLogin(); resolve(reply([ride104])); await pending;
  assert.equal(h.read('state.history.length'), 0); assert.equal(h.read('state.ridePages.history'), null);
  assert.equal(h.node('#history-body').innerHTML, '');
});

test('record names and dates cannot inject HTML into either table', async () => {
  const h = harness(); h.context.execute = async () => reply([{ ...ride104, customerName: '<img onerror=attack()>', driverName: '<script>attack()</script>', acceptedAtUtc: '<script>attack()</script>' }]);
  await h.context.loadRides(); await h.context.loadHistory();
  for (const selector of ['#rides-body', '#history-body']) {
    assert.doesNotMatch(h.node(selector).innerHTML, /<script>|<img/); assert.match(h.node(selector).innerHTML, /&lt;/);
  }
});

test('navigation cancelling a refresh preserves the older page and restores its pager', async () => {
  const h = harness(); h.context.execute = async () => reply([ride104], 3, 207); await h.context.loadHistory(3);
  h.context.execute = async () => { throw h.read("new AdminOperationCancelled('تجاوز تحديث أحدث هذه القراءة.')"); };
  await h.context.loadHistory(3);
  assert.equal(h.read('state.ridePages.history.pageNumber'), 3); assert.match(h.node('#history-body').innerHTML, /#104/);
  assert.equal(h.node('#history-page-previous').disabled, false); assert.equal(h.node('#history-page-next').disabled, true);
  assert.equal(h.node('#history-list-refresh').disabled, false); assert.equal(h.node('#history-list-feedback').classList.contains('error'), false);
});
