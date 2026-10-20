// Administrative report uses persisted stop policies and final per-stop fees.
let waitingReportPage = 1;
let waitingReportPageCount = 1;
let waitingReportRevision = 0;
let waitingReportAppliedFilters = {};
const waitingReportReasons = {
  driver_resumed: 'استأنف السائق الرحلة', vehicle_moving: 'تحركت المركبة',
  connection_lost: 'انقطع تحديث موقع السائق', fee_cap: 'بلغ سقف الرسوم',
  ride_closed: 'انتهت أو أُلغيت الرحلة', rejected: 'رُفض الطلب',
  ride_cancelled: 'أُلغيت الرحلة',
  withdrawn: 'سُحب الطلب', request_expired: 'انتهت مهلة الموافقة'
};
function waitingReportDuration(value) {
  const seconds = Math.max(0, Math.floor(Number(value) || 0));
  const hours = Math.floor(seconds / 3600);
  return `${hours ? `${number(hours)} س ` : ''}${number(Math.floor(seconds % 3600 / 60))} د ${number(seconds % 60)} ث`;
}
function waitingReportDate(value) {
  return value ? new Intl.DateTimeFormat('ar-YE-u-nu-latn', {
    dateStyle: 'short', timeStyle: 'medium', timeZone: 'Asia/Aden'
  }).format(new Date(value)) : '—';
}
function waitingReportFilters() {
  const from = $('#waiting-report-from').value;
  const to = $('#waiting-report-to').value;
  if (from && to && from > to) throw new Error('تاريخ البداية يجب أن يسبق تاريخ النهاية أو يساويه.');
  const ride = $('#waiting-report-ride').value;
  if (ride && (!Number.isSafeInteger(Number(ride)) || Number(ride) < 1)) throw new Error('رقم الرحلة يجب أن يكون عددًا صحيحًا موجبًا.');
  return {
    fromUtc: from ? new Date(`${from}T00:00:00+03:00`).toISOString() : null,
    toUtc: to ? new Date(new Date(`${to}T00:00:00+03:00`).getTime() + 86400000).toISOString() : null,
    rideId: ride ? Number(ride) : null, search: $('#waiting-report-search').value.trim() || null,
    status: $('#waiting-report-status').value || null, chargedOnly: $('#waiting-report-charged').checked
  };
}
function waitingReportRow(row) {
  const lat = Number(row.latitude), lng = Number(row.longitude);
  const hasLocation = row.latitude != null && row.longitude != null && Number.isFinite(lat) && Number.isFinite(lng) && Math.abs(lat) <= 90 && Math.abs(lng) <= 180;
  const location = hasLocation ? `<a class="button" href="https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(`${lat},${lng}`)}" target="_blank" rel="noopener noreferrer">عرض على الخريطة</a><span class="cell-sub" dir="ltr">${lat.toFixed(5)}, ${lng.toFixed(5)}</span>` : 'الموقع غير متاح';
  const statuses = { pending: 'بانتظار الموافقة', active: 'انتظار جارٍ', ended: 'انتظار منتهٍ', unstarted: 'طلب لم يبدأ' };
  return `<tr>
    <td><span class="cell-main">رحلة #${escapeHtml(row.rideId)}</span><span class="cell-sub">${escapeHtml(row.serviceName)} · ${escapeHtml(rideStatuses[row.rideStatus] || '')}</span><span class="cell-sub">من: ${escapeHtml(row.pickup)}</span><span class="cell-sub">إلى: ${escapeHtml(row.destination)}</span></td>
    <td><span class="cell-main">${escapeHtml(row.customerName || '—')}</span><span class="cell-sub">السائق: ${escapeHtml(row.driverName || '—')}</span></td>
    <td>${location}</td>
    <td>${badge(statuses[row.status] || '—', row.status === 'active' ? 'warning' : '')}<span class="cell-sub">طلب: ${waitingReportDate(row.createdAtUtc)}</span><span class="cell-sub">بداية: ${waitingReportDate(row.startedAtUtc)}</span><span class="cell-sub">نهاية: ${waitingReportDate(row.endedAtUtc)}</span><span class="cell-sub">${escapeHtml(waitingReportReasons[row.endReason] || '')}</span></td>
    <td><span class="cell-main">${waitingReportDuration(row.durationSeconds)}</span><span class="cell-sub">مجاني: ${waitingReportDuration(row.freeSeconds)}</span><span class="cell-sub">بعد المهلة: ${waitingReportDuration(row.chargeableSeconds)}</span></td>
    <td><span class="cell-main">${number(row.pricePerMinute)} ر.ي / دقيقة</span><span class="cell-sub">المهلة: ${number(row.freeMinutes)} دقيقة</span><span class="cell-sub">سقف هذا التوقف: ${number(row.maxFee)} ر.ي</span></td>
    <td><span class="cell-main">${number(row.chargedFee)} ر.ي</span>${row.estimatedFee != null ? `<span class="cell-sub">تقديري غير مثبت: ${number(row.estimatedFee)} ر.ي</span>` : ''}<span class="cell-sub">إجمالي أجرة الرحلة: ${number(row.totalAmount)} ر.ي</span></td>
  </tr>`;
}
async function loadWaitingReport() {
  const revision = ++waitingReportRevision;
  $('#waiting-report-feedback').textContent = 'جارٍ تحميل التقرير…';
  $('#waiting-report-prev').disabled = true;
  $('#waiting-report-next').disabled = true;
  $('#view-waiting-report').setAttribute('aria-busy', 'true');
  try {
    const result = await execute('RideWaitingReportModel', 'report', { ...waitingReportAppliedFilters, page: waitingReportPage, pageSize: 25 });
    if (revision !== waitingReportRevision) return;
    const report = result.data;
    waitingReportPage = report.page;
    waitingReportPageCount = report.pageCount;
    const summary = report.summary;
    $('#waiting-report-summary').innerHTML = [
      ['طلبات التوقف', number(summary.sessionCount)], ['الرحلات', number(summary.rideCount)],
      ['انتظار جارٍ', number(summary.activeCount)], ['بانتظار الموافقة', number(summary.pendingCount)],
      ['مدة الانتظار المنتهي', waitingReportDuration(summary.endedSeconds)], ['رسوم مثبتة', `${number(summary.chargedFee)} ر.ي`]
    ].map(([label, value]) => `<div><span>${label}</span><strong>${value}</strong></div>`).join('');
    toggleEmpty('#waiting-report-body', '#waiting-report-empty', (report.rows || []).map(waitingReportRow));
    $('#waiting-report-page').textContent = `صفحة ${number(report.page)} من ${number(report.pageCount)} · ${number(report.totalCount)} طلب`;
    $('#waiting-report-prev').disabled = report.page <= 1;
    $('#waiting-report-next').disabled = report.page >= report.pageCount;
    $('#waiting-report-feedback').textContent = `الإجماليات تشمل جميع النتائج المطابقة للتصفية. آخر تحديث: ${waitingReportDate(report.serverNowUtc)}`;
  } catch (error) {
    if (revision !== waitingReportRevision) return;
    $('#waiting-report-body').innerHTML = '';
    $('#waiting-report-summary').innerHTML = '';
    $('#waiting-report-page').textContent = '';
    $('#waiting-report-empty').classList.add('hidden');
    $('#waiting-report-feedback').textContent = `تعذر تحميل التقرير: ${error.message}`;
  } finally {
    if (revision === waitingReportRevision) $('#view-waiting-report').removeAttribute('aria-busy');
  }
}
$('#waiting-report-filters').addEventListener('submit', event => {
  event.preventDefault();
  try { waitingReportAppliedFilters = waitingReportFilters(); waitingReportPage = 1; loadWaitingReport(); }
  catch (error) { $('#waiting-report-feedback').textContent = error.message; }
});
$('#waiting-report-filters').addEventListener('reset', () => {
  waitingReportAppliedFilters = {}; waitingReportPage = 1; loadWaitingReport();
});
$('#waiting-report-refresh').addEventListener('click', () => loadWaitingReport());
$('#waiting-report-prev').addEventListener('click', () => { if (waitingReportPage > 1) { waitingReportPage--; loadWaitingReport(); } });
$('#waiting-report-next').addEventListener('click', () => { if (waitingReportPage < waitingReportPageCount) { waitingReportPage++; loadWaitingReport(); } });
