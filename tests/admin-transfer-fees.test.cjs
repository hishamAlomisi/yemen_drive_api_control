const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync(require('node:path').join(__dirname, '../src/YemenDrive.Api/wwwroot/admin-transfer-fees.js'), 'utf8');
function harness({ rules = [], writable = true, fail = false } = {}) {
  const nodes = new Map(), calls = [], fields = new Map();
  function node(selector) {
    if (!nodes.has(selector)) nodes.set(selector, { value: '', checked: false, disabled: false, textContent: '', innerHTML: '', events: {},
      addEventListener(name, fn) { this.events[name] = fn; }, querySelector() { return node('submit'); }, reset() { fields.clear(); }, scrollIntoView() {},
      elements: { namedItem(name) { return node(name); } } });
    return nodes.get(selector);
  }
  const context = vm.createContext({ document: { querySelector: node, querySelectorAll: () => [node('submit')] }, canAdmin: () => writable,
    number: n => String(n), escapeHtml: v => String(v).replaceAll('<', '&lt;').replaceAll('>', '&gt;'), toast() {},
    FormData: class { get(k) { return fields.get(k) ?? ''; } has(k) { return fields.has(k); } },
    execute: async (model, op, data) => { calls.push({ model, op, data }); if (fail && op !== 'list') throw Error('تداخل الشرائح'); return { data: op === 'list' ? rules : data }; } });
  vm.runInContext(source, context);
  return { node, calls, fields, load: () => vm.runInContext('loadTransferFees()', context),
    submit: () => node('#transfer-fee-form').events.submit({ preventDefault() {}, currentTarget: node('#transfer-fee-form') }) };
}
test('empty fee configuration explicitly disables transfers', async () => {
  const h = harness(); await h.load(); assert.match(h.node('#transfer-fee-body').innerHTML, /التحويل غير متاح/);
});
test('finance read permission leaves editing disabled and escapes values', async () => {
  const h = harness({ writable: false, rules: [{ id: 1, fromAmount: '<script>', toAmount: null, feeValue: 2, feeKind: 'Percent', isActive: true }] });
  await h.load(); assert.equal(h.node('submit').disabled, true); assert.doesNotMatch(h.node('#transfer-fee-body').innerHTML, /<script>/); assert.match(h.node('#transfer-fee-body').innerHTML, /٪/);
});
test('open tier and free fixed fee are sent as null upper bound and numeric zero', async () => {
  const h = harness(); h.fields.set('fromAmount', '1000'); h.fields.set('feeKind', 'Fixed'); h.fields.set('feeValue', '0'); h.fields.set('isActive', 'on');
  await h.submit(); const c = h.calls[0]; assert.equal(c.model, 'WalletTransferFeeModel'); assert.equal(c.op, 'add'); assert.equal(c.data.toAmount, null); assert.equal(c.data.feeValue, 0); assert.equal(c.data.isActive, true);
});
test('editing preserves id and deactivation and shows server validation', async () => {
  const h = harness({ fail: true }); h.fields.set('id', '7'); h.fields.set('fromAmount', '10'); h.fields.set('toAmount', '50'); h.fields.set('feeKind', 'Percent'); h.fields.set('feeValue', '2.5');
  await h.submit(); assert.equal(h.calls[0].op, 'update'); assert.equal(h.calls[0].data.id, 7); assert.equal(h.calls[0].data.isActive, false); assert.equal(h.node('#transfer-fee-result').textContent, 'تداخل الشرائح'); assert.equal(h.node('submit').disabled, false);
});
