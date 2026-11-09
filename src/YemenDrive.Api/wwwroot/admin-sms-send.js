let smsSendGeneration = 0;
let smsSendBusy = false;
let smsSendConnected = false;
let smsSendAttempt = null;

function smsSendResult(message, error = false) {
  const element = $('#sms-send-result');
  element.textContent = message;
  element.classList.toggle('hidden', !message);
  element.classList.toggle('error', error);
}

function resetSmsSend() {
  smsSendGeneration++;
  smsSendConnected = false;
  smsSendAttempt = null;
  $('#sms-send-form').reset();
  $('#sms-send-fields').disabled = true;
  $('#sms-send-connection').textContent = 'جارٍ التحقق من اتصال المزود...';
  $('#sms-send-text-count').textContent = '0 / 1600 حرف';
  $('#sms-send-submit').textContent = 'إرسال الرسالة';
  smsSendResult('');
}

async function loadSmsSend() {
  if (smsSendBusy) return;
  const generation = ++smsSendGeneration;
  const administratorId = currentAdministratorId;
  smsSendConnected = false;
  $('#sms-send-fields').disabled = true;
  $('#sms-send-connection').textContent = 'جارٍ التحقق من اتصال المزود...';
  try {
    if (!canAdmin('operations.write')) throw new Error('لا تملك صلاحية إرسال الرسائل النصية.');
    const response = await adminFetch('/api/admin/sms-send', { cache: 'no-store' });
    const result = await response.json().catch(() => null);
    if (generation !== smsSendGeneration || administratorId !== currentAdministratorId || state.view !== 'sms-send') return;
    if (!response.ok || !result?.success) throw new Error(result?.message || 'تعذر التحقق من اتصال مزود الرسائل.');
    smsSendConnected = result.data.connected === true;
    $('#sms-send-connection').textContent = result.data.message || 'تعذر الاتصال بالمزود.';
    $('#sms-send-fields').disabled = !smsSendConnected || !canAdmin('operations.write');
  } catch (error) {
    if (generation === smsSendGeneration && administratorId === currentAdministratorId) $('#sms-send-connection').textContent = error.message;
  }
}

function smsSendNumbers(value) {
  return value.split(/[\r\n,،;؛]+/).map(number => number.trim()).filter(Boolean);
}

function newSmsRequestKey() {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  // getRandomValues is available on HTTP LAN/domain pages as well as localhost.
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 15) | 64;
  bytes[8] = (bytes[8] & 63) | 128;
  const hex = [...bytes].map(value => value.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

$('#sms-send-refresh').addEventListener('click', () => { void loadSmsSend(); });
$('#sms-send-clear').addEventListener('click', () => {
  if (smsSendBusy) return;
  $('#sms-send-form').reset(); smsSendAttempt = null;
  $('#sms-send-submit').textContent = 'إرسال الرسالة';
  $('#sms-send-text-count').textContent = '0 / 1600 حرف'; smsSendResult('');
});
$('#sms-send-form').elements.namedItem('text').addEventListener('input', event => {
  $('#sms-send-text-count').textContent = `${event.target.value.length} / 1600 حرف`;
});

$('#sms-send-form').addEventListener('submit', async event => {
  event.preventDefault();
  if (smsSendBusy || !smsSendConnected || !canAdmin('operations.write')) return;
  const form = event.currentTarget;
  const numbers = smsSendNumbers(form.elements.namedItem('numbers').value);
  const text = form.elements.namedItem('text').value;
  if (numbers.length < 1 || numbers.length > 100) { smsSendResult('أدخل من رقم واحد إلى 100 رقم، مفصولة بأسطر أو فواصل.', true); return; }
  if (!text.trim() || text.length > 1600) { smsSendResult('أدخل نص الرسالة بما لا يتجاوز 1600 حرف.', true); return; }
  const contentKey = JSON.stringify({ numbers, text });
  if (!smsSendAttempt || smsSendAttempt.contentKey !== contentKey) smsSendAttempt = { contentKey, requestKey: newSmsRequestKey() };
  const generation = smsSendGeneration;
  const administratorId = currentAdministratorId;
  smsSendBusy = true;
  $('#sms-send-fields').disabled = true;
  $('#sms-send-refresh').disabled = true;
  smsSendResult('جارٍ إرسال الرسالة...');
  try {
    const response = await adminFetch('/api/admin/sms-send', { method: 'POST', cache: 'no-store', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ numbers, text, requestKey: smsSendAttempt.requestKey }) });
    const result = await response.json().catch(() => null);
    if (generation !== smsSendGeneration || administratorId !== currentAdministratorId || state.view !== 'sms-send') return;
    if (!response.ok || !result?.success) throw new Error(result?.message || 'تعذر معرفة نتيجة طلب الإرسال.');
    const data = result.data;
    smsSendResult(`${data.message} معرف الرسالة: ${data.messageId}.`, !data.accepted);
    if (data.accepted) {
      form.elements.namedItem('text').value = ''; smsSendAttempt = null;
      $('#sms-send-text-count').textContent = '0 / 1600 حرف';
      $('#sms-send-submit').textContent = 'إرسال الرسالة';
    } else if (data.uncertain) $('#sms-send-submit').textContent = 'تحقق من نتيجة الطلب';
    else { smsSendAttempt = null; $('#sms-send-submit').textContent = 'إعادة الإرسال'; }
  } catch (error) {
    if (generation === smsSendGeneration && administratorId === currentAdministratorId) {
      smsSendResult(error.message + ' يمكنك التحقق من الطلب نفسه دون تكرار الإرسال.', true);
      $('#sms-send-submit').textContent = 'تحقق من نتيجة الطلب';
    }
  } finally {
    smsSendBusy = false;
    $('#sms-send-refresh').disabled = false;
    $('#sms-send-fields').disabled = !smsSendConnected || !canAdmin('operations.write');
    if (generation !== smsSendGeneration && state.view === 'sms-send') void loadSmsSend();
  }
});
