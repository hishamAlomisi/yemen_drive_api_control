let smsReportGeneration = 0;
const smsReportFields = {
  signInCount: '#sms-report-sign-in', passwordResetCount: '#sms-report-password-reset',
  registrationCount: '#sms-report-registration', walletMovementCount: '#sms-report-wallet',
  otherOtpCount: '#sms-report-other-otp', manualCount: '#sms-report-manual', totalCount: '#sms-report-total'
};

function resetSmsReport() {
  smsReportGeneration++;
  for (const selector of Object.values(smsReportFields)) $(selector).textContent = '—';
  $('#sms-report-result').textContent = 'جارٍ تحميل التقرير...';
  $('#sms-report-result').classList.remove('error');
  $('#sms-report-refresh').disabled = false;
}

async function loadSmsReport() {
  resetSmsReport();
  const generation = smsReportGeneration;
  const administratorId = currentAdministratorId;
  $('#sms-report-refresh').disabled = true;
  try {
    if (!canAdmin('operations.read')) throw new Error('لا تملك صلاحية عرض تقرير الرسائل النصية.');
    const response = await adminFetch('/api/admin/sms-statistics', { cache: 'no-store' });
    const result = await response.json().catch(() => null);
    if (!response.ok || !result?.success) throw new Error(result?.message || 'تعذر تحميل تقرير الرسائل النصية.');
    if (generation !== smsReportGeneration || administratorId !== currentAdministratorId || state.view !== 'sms-report') return;
    for (const [field, selector] of Object.entries(smsReportFields)) $(selector).textContent = number(result.data[field] ?? 0);
    $('#sms-report-result').textContent = result.data.totalCount === 0
      ? 'لا توجد رسائل نصية مسجلة حتى الآن.'
      : `آخر رسالة نصية مسجلة: ${date(result.data.lastSentAtUtc)}`;
  } catch (error) {
    if (generation === smsReportGeneration) {
      $('#sms-report-result').textContent = error.message;
      $('#sms-report-result').classList.add('error');
    }
  } finally {
    if (generation === smsReportGeneration) $('#sms-report-refresh').disabled = false;
  }
}

$('#sms-report-refresh').addEventListener('click', () => { void loadSmsReport(); });
