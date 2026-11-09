let smsProviderGeneration = 0;
let smsProviderSaving = false;
let smsProviderLoaded = false;
let smsConnectionTimer = null;
let smsConnectionBusy = false;
let smsActivationDismissed = false;
let smsProviderConfigured = false;

function smsProviderMessage(message, error = false) {
  const result = $('#sms-provider-result');
  result.textContent = message;
  result.classList.toggle('error', error);
}

function resetSmsProviderForm() {
  smsProviderGeneration++;
  smsProviderLoaded = false;
  smsProviderConfigured = false;
  clearTimeout(smsConnectionTimer);
  smsConnectionTimer = null;
  $('#sms-activation-modal').close();
  $('#sms-activation-form').reset();
  $('#sms-activation-result').textContent = '';
  $('#sms-provider-connection').textContent = '';
  $('#sms-provider-connect').disabled = true;
  smsActivationDismissed = false;
  $('#sms-provider-form').reset();
  $('#sms-provider-fields').disabled = true;
  smsProviderMessage('جارٍ تحميل البيانات...');
}

function renderSmsProviderSettings(data) {
  const form = $('#sms-provider-form');
  for (const name of ['url', 'username', 'deviceNumber']) form.elements.namedItem(name).value = data[name] || '';
  const password = form.elements.namedItem('password');
  password.value = '';
  password.required = !data.hasPassword;
  password.placeholder = data.hasPassword ? 'اتركها فارغة للاحتفاظ بكلمة المرور' : '';
  $('#sms-provider-password-hint').textContent = data.hasPassword
    ? 'كلمة المرور محفوظة. أدخل قيمة جديدة لتغييرها.' : 'أدخل كلمة مرور المزود.';
  smsProviderLoaded = true;
  smsProviderConfigured = data.hasPassword === true;
  $('#sms-provider-fields').disabled = smsProviderSaving || !canAdmin('policy.write');
  $('#sms-provider-connect').disabled = !data.hasPassword || !canAdmin('policy.write') || smsConnectionBusy;
  renderSmsConnection(data.connection);
}

function renderSmsConnection(connection) {
  if (!connection?.state) return;
  $('#sms-provider-connection').textContent = connection.message || '';
  if (connection.requiresActivation && canAdmin('policy.write') && state.view === 'sms-provider' && !smsActivationDismissed && !$('#sms-activation-modal').open) {
    $('#sms-activation-form').reset();
    $('#sms-activation-result').textContent = '';
    $('#sms-activation-modal').showModal();
  } else if (connection.connected) $('#sms-activation-modal').close();
}

function scheduleSmsConnectionCheck() {
  clearTimeout(smsConnectionTimer);
  if (state.view !== 'sms-provider' || !smsProviderLoaded) return;
  const generation = smsProviderGeneration;
  const administratorId = currentAdministratorId;
  smsConnectionTimer = setTimeout(async () => {
    try {
      if (!smsProviderSaving && !smsConnectionBusy && canAdmin('policy.read')) {
        const response = await adminFetch('/api/admin/sms-provider/connection', { cache: 'no-store' });
        const result = await response.json().catch(() => null);
        if (generation !== smsProviderGeneration || administratorId !== currentAdministratorId || state.view !== 'sms-provider') return;
        if (response.ok && result?.success) renderSmsConnection(result.data);
      }
    } catch { /* The next check refreshes connection status. */ }
    if (generation === smsProviderGeneration) scheduleSmsConnectionCheck();
  }, 5000);
}

async function smsProviderRequest(options = {}) {
  const response = await adminFetch('/api/admin/sms-provider', { ...options, cache: 'no-store' });
  const result = await response.json().catch(() => null);
  if (!response.ok || !result?.success) throw new Error(result?.message || 'تعذر تحميل أو حفظ بيانات مزود الرسائل.');
  return result;
}

async function loadSmsProviderSettings() {
  if (smsProviderSaving) return;
  const generation = ++smsProviderGeneration;
  const administratorId = currentAdministratorId;
  smsProviderLoaded = false;
  $('#sms-provider-fields').disabled = true;
  smsProviderMessage('جارٍ تحميل البيانات...');
  try {
    if (!canAdmin('policy.read')) throw new Error('لا تملك صلاحية عرض بيانات مزود الرسائل.');
    const result = await smsProviderRequest();
    if (generation !== smsProviderGeneration || administratorId !== currentAdministratorId || state.view !== 'sms-provider') return;
    renderSmsProviderSettings(result.data);
    smsProviderMessage(result.data.hasPassword
      ? 'بيانات المزود محفوظة ويمكن تعديلها.' : 'لم تُحفظ بيانات مزود الرسائل بعد.');
    scheduleSmsConnectionCheck();
  } catch (error) {
    if (generation === smsProviderGeneration) smsProviderMessage(error.message, true);
  }
}

$('#sms-provider-form').addEventListener('submit', async event => {
  event.preventDefault();
  if (smsProviderSaving || smsConnectionBusy || !smsProviderLoaded || !canAdmin('policy.write')) return;
  const generation = ++smsProviderGeneration;
  const administratorId = currentAdministratorId;
  const form = event.currentTarget;
  const data = Object.fromEntries(['url', 'username', 'password', 'deviceNumber'].map(name => [name, form.elements.namedItem(name).value]));
  smsProviderSaving = true;
  smsActivationDismissed = false;
  $('#sms-provider-fields').disabled = true;
  smsProviderMessage('جارٍ حفظ البيانات...');
  try {
    const result = await smsProviderRequest({ method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) });
    if (generation !== smsProviderGeneration || administratorId !== currentAdministratorId) return;
    renderSmsProviderSettings(result.data);
    smsProviderMessage(result.message);
  } catch (error) {
    if (generation === smsProviderGeneration) smsProviderMessage(error.message, true);
  } finally {
    data.password = '';
    form.elements.namedItem('password').value = '';
    smsProviderSaving = false;
    $('#sms-provider-fields').disabled = !smsProviderLoaded || !canAdmin('policy.write');
    if (generation !== smsProviderGeneration && state.view === 'sms-provider' && canAdmin('policy.read')) void loadSmsProviderSettings();
    if (generation === smsProviderGeneration) scheduleSmsConnectionCheck();
  }
});

$('#sms-activation-modal').addEventListener('close', () => {
  $('#sms-activation-form').reset();
  $('#sms-activation-result').textContent = '';
});
$('#sms-activation-modal').addEventListener('cancel', () => { smsActivationDismissed = true; });
$('#sms-activation-modal').querySelectorAll('[data-close]').forEach(button => button.addEventListener('click', () => { smsActivationDismissed = true; }));

async function changeSmsConnection(path, body) {
  if (smsConnectionBusy || smsProviderSaving || !canAdmin('policy.write')) return;
  const generation = smsProviderGeneration;
  const administratorId = currentAdministratorId;
  smsConnectionBusy = true;
  $('#sms-provider-fields').disabled = true;
  $('#sms-provider-connect').disabled = true;
  $('#sms-activation-form').querySelector('[type=submit]').disabled = true;
  try {
    const response = await adminFetch('/api/admin/sms-provider/' + path, { method: 'POST', cache: 'no-store',
      headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body || {}) });
    const result = await response.json().catch(() => null);
    if (generation !== smsProviderGeneration || administratorId !== currentAdministratorId || state.view !== 'sms-provider') return;
    if (!response.ok || !result?.success) throw new Error(result?.message || 'تعذر الاتصال بمزود الرسائل.');
    renderSmsConnection(result.data);
    if (!result.data.connected && $('#sms-activation-modal').open) $('#sms-activation-result').textContent = result.data.message;
  } catch (error) {
    if (generation === smsProviderGeneration) {
      $('#sms-provider-connection').textContent = error.message;
      if ($('#sms-activation-modal').open) $('#sms-activation-result').textContent = error.message;
    }
  } finally {
    if (body) body.code = '';
    $('#sms-activation-form').elements.namedItem('code').value = '';
    smsConnectionBusy = false;
    $('#sms-activation-form').querySelector('[type=submit]').disabled = false;
    $('#sms-provider-fields').disabled = !smsProviderLoaded || !canAdmin('policy.write');
    $('#sms-provider-connect').disabled = !smsProviderLoaded || !smsProviderConfigured || !canAdmin('policy.write');
    if (generation === smsProviderGeneration) scheduleSmsConnectionCheck();
  }
}

$('#sms-provider-connect').addEventListener('click', () => { smsActivationDismissed = false; void changeSmsConnection('connect'); });
$('#sms-activation-form').addEventListener('submit', event => {
  event.preventDefault();
  void changeSmsConnection('activate', { code: event.currentTarget.elements.namedItem('code').value.trim() });
});
