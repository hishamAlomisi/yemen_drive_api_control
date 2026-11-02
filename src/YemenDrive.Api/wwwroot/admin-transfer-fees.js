let transferFeeRules = [];
async function loadTransferFees() {
  const result = await execute('WalletTransferFeeModel', 'list', {});
  transferFeeRules = result.data || [];
  const writable = canAdmin('finance.write');
  document.querySelectorAll('#transfer-fee-form input, #transfer-fee-form select, #transfer-fee-form button').forEach(n => { n.disabled = !writable; });
  document.querySelector('#transfer-fee-body').innerHTML = transferFeeRules.length ? transferFeeRules.map(r => `<tr><td>${escapeHtml(number(r.fromAmount))}</td><td>${r.toAmount == null ? 'بدون حد أعلى' : escapeHtml(number(r.toAmount))}</td><td>${escapeHtml(number(r.feeValue))} ${r.feeKind === 'Percent' ? '٪' : 'ر.ي'}</td><td>${r.isActive ? 'مفعلة' : 'موقوفة'}</td><td><button type="button" class="button small" data-transfer-fee-id="${Number(r.id)}" ${writable ? '' : 'disabled'}>تعديل</button></td></tr>`).join('') : '<tr><td colspan="5">لم تُضف شرائح رسوم. التحويل غير متاح حتى إعداد شريحة مفعلة.</td></tr>';
}
function resetTransferFeeForm() {
  document.querySelector('#transfer-fee-form').reset();
  document.querySelector('#transfer-fee-form [name=id]').value = '';
  document.querySelector('#transfer-fee-result').textContent = '';
}
document.querySelector('#transfer-fee-body').addEventListener('click', event => {
  const button = event.target.closest('[data-transfer-fee-id]'); if (!button) return;
  const rule = transferFeeRules.find(r => r.id === Number(button.dataset.transferFeeId)); if (!rule) return;
  const form = document.querySelector('#transfer-fee-form');
  for (const name of ['id', 'fromAmount', 'toAmount', 'feeKind', 'feeValue']) form.elements.namedItem(name).value = rule[name] ?? '';
  form.elements.namedItem('isActive').checked = rule.isActive;
  form.scrollIntoView({ behavior: 'smooth', block: 'start' });
});
document.querySelector('#transfer-fee-reset').addEventListener('click', resetTransferFeeForm);
document.querySelector('#transfer-fee-refresh').addEventListener('click', () => loadTransferFees().catch(e => toast(e.message, true)));
document.querySelector('#transfer-fee-form').addEventListener('submit', async event => {
  event.preventDefault();
  const form = event.currentTarget, fields = new FormData(form), button = form.querySelector('[type=submit]');
  const data = { id: fields.get('id') ? Number(fields.get('id')) : null, fromAmount: Number(fields.get('fromAmount')),
    toAmount: fields.get('toAmount') === '' ? null : Number(fields.get('toAmount')), feeKind: fields.get('feeKind'),
    feeValue: Number(fields.get('feeValue')), isActive: fields.has('isActive') };
  button.disabled = true;
  try { await execute('WalletTransferFeeModel', data.id ? 'update' : 'add', data); resetTransferFeeForm(); await loadTransferFees(); document.querySelector('#transfer-fee-result').textContent = 'تم حفظ شريحة الرسوم.'; }
  catch (e) { document.querySelector('#transfer-fee-result').textContent = e.message; }
  finally { button.disabled = !canAdmin('finance.write'); }
});
