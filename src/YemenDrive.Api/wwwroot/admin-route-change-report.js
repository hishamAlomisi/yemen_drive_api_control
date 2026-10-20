// Read-only report of saved route-change requests and their invoice snapshots.
let routeChangeReportPage = 1;
let routeChangeReportPageCount = 1;
let routeChangeReportRevision = 0;
let routeChangeReportAppliedFilters = {};
let routeChangeReportRows = [];
const routeChangeReportStatuses = {
  quoted: 'عرض أجرة', pending: 'بانتظار الموافقة', accepted: 'مقبول',
  rejected: 'مرفوض', cancelled: 'ملغى', expired: 'انتهت المهلة'
};
function routeChangeReportDate(value) {
  const dateValue = value ? new Date(value) : null;
  return dateValue && Number.isFinite(dateValue.getTime()) ? new Intl.DateTimeFormat('ar-YE-u-nu-latn', {
    dateStyle: 'short', timeStyle: 'medium', timeZone: 'Asia/Aden'
  }).format(dateValue) : '—';
}
function routeChangeReportFilters() {
  const from = $('#route-change-report-from').value;
  const to = $('#route-change-report-to').value;
  if (from && to && from > to) throw new Error('تاريخ البداية يجب أن يسبق تاريخ النهاية أو يساويه.');
  const ride = $('#route-change-report-ride').value;
  if (ride && (!Number.isSafeInteger(Number(ride)) || Number(ride) < 1)) throw new Error('رقم الرحلة يجب أن يكون عددًا صحيحًا موجبًا.');
  return {
    fromUtc: from ? new Date(`${from}T00:00:00+03:00`).toISOString() : null,
    toUtc: to ? new Date(new Date(`${to}T00:00:00+03:00`).getTime() + 86400000).toISOString() : null,
    rideId: ride ? Number(ride) : null, search: $('#route-change-report-search').value.trim() || null,
    status: $('#route-change-report-status').value || null
  };
}
function routeChangeReportRow(row) {
  const lat = Number(row.changeLatitude), lng = Number(row.changeLongitude);
  const hasLocation = row.changeLatitude != null && row.changeLongitude != null && Number.isFinite(lat) && Number.isFinite(lng) && Math.abs(lat) <= 90 && Math.abs(lng) <= 180;
  const location = hasLocation ? `<a class="button" href="https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(`${lat},${lng}`)}" target="_blank" rel="noopener noreferrer">عرض على الخريطة</a><span class="cell-sub" dir="ltr">${lat.toFixed(5)}, ${lng.toFixed(5)}</span>` : 'الموقع غير متاح';
  const invoice = row.invoice;
  return `<tr>
    <td><span class="cell-main">رحلة #${escapeHtml(row.rideId)}</span><span class="cell-sub">طلب #${escapeHtml(row.id)}</span><span class="cell-sub">${escapeHtml(row.customerName || '—')}</span><span class="cell-sub">السائق: ${escapeHtml(row.driverName || '—')}</span></td>
    <td><span class="cell-sub">الوجهة السابقة</span><span class="cell-main">${escapeHtml(row.oldDestinationLabel || '—')}</span><span class="cell-sub">الوجهة الجديدة</span><span class="cell-main">${escapeHtml(row.newDestinationLabel || '—')}</span></td>
    <td>${location}</td>
    <td>${badge(routeChangeReportStatuses[row.status] || '—', row.status === 'accepted' ? 'success' : row.status === 'pending' ? 'warning' : '')}<span class="cell-sub">الطلب: ${routeChangeReportDate(row.createdAtUtc)}</span><span class="cell-sub">القرار: ${routeChangeReportDate(row.decidedAtUtc)}</span><span class="cell-sub">المهلة: ${routeChangeReportDate(row.expiresAtUtc)}</span></td>
    <td>${invoice ? `<span class="cell-main">${number(invoice.routeChangeFee)} ر.ي</span><span class="cell-sub">${row.status === 'accepted' ? 'أُضيفت لأجرة الراكب' : 'غير مثبتة على الراكب'}</span>` : '—'}</td>
    <td>${invoice ? `<span class="cell-main">${number(invoice.newTotal)} ر.ي</span><span class="cell-sub">السابق: ${number(invoice.previousTotal)} ر.ي</span><span class="cell-sub">الفرق: ${number(invoice.difference)} ر.ي</span><button class="button" type="button" data-route-change-invoice="${escapeHtml(row.id)}">تفاصيل الفاتورة</button>` : 'الفاتورة غير متاحة'}</td>
  </tr>`;
}
async function loadRouteChangeReport() {
  const revision = ++routeChangeReportRevision;
  $('#route-change-report-feedback').textContent = 'جارٍ تحميل السجل…';
  $('#route-change-report-prev').disabled = true;
  $('#route-change-report-next').disabled = true;
  $('#view-route-change-report').setAttribute('aria-busy', 'true');
  try {
    const result = await execute('RideRouteChangeReportModel', 'report', { ...routeChangeReportAppliedFilters, page: routeChangeReportPage, pageSize: 25 });
    if (revision !== routeChangeReportRevision) return;
    const report = result.data;
    routeChangeReportPage = report.page;
    routeChangeReportPageCount = report.pageCount;
    routeChangeReportRows = report.rows || [];
    $('#route-change-report-summary').innerHTML = [
      ['طلبات تغيير المسار', number(report.summary.requestCount)], ['طلبات مقبولة', number(report.summary.acceptedCount)],
      ['رسوم تغيير مثبتة', `${number(report.summary.chargedFee)} ر.ي`]
    ].map(([label, value]) => `<div><span>${label}</span><strong>${value}</strong></div>`).join('');
    toggleEmpty('#route-change-report-body', '#route-change-report-empty', routeChangeReportRows.map(routeChangeReportRow));
    $('#route-change-report-page').textContent = `صفحة ${number(report.page)} من ${number(report.pageCount)} · ${number(report.totalCount)} طلب`;
    $('#route-change-report-prev').disabled = report.page <= 1;
    $('#route-change-report-next').disabled = report.page >= report.pageCount;
    $('#route-change-report-feedback').textContent = `الإجماليات تشمل جميع النتائج المطابقة للتصفية. آخر تحديث: ${routeChangeReportDate(report.serverNowUtc)}`;
  } catch (error) {
    if (revision !== routeChangeReportRevision) return;
    routeChangeReportRows = [];
    $('#route-change-report-body').innerHTML = '';
    $('#route-change-report-summary').innerHTML = '';
    $('#route-change-report-page').textContent = '';
    $('#route-change-report-empty').classList.add('hidden');
    $('#route-change-report-feedback').textContent = `تعذر تحميل السجل: ${error.message}`;
  } finally {
    if (revision === routeChangeReportRevision) $('#view-route-change-report').removeAttribute('aria-busy');
  }
}
$('#route-change-report-filters').addEventListener('submit', event => {
  event.preventDefault();
  try { routeChangeReportAppliedFilters = routeChangeReportFilters(); routeChangeReportPage = 1; loadRouteChangeReport(); }
  catch (error) { $('#route-change-report-feedback').textContent = error.message; }
});
$('#route-change-report-filters').addEventListener('reset', () => {
  routeChangeReportAppliedFilters = {}; routeChangeReportPage = 1; loadRouteChangeReport();
});
$('#route-change-report-refresh').addEventListener('click', () => loadRouteChangeReport());
$('#route-change-report-prev').addEventListener('click', () => { if (routeChangeReportPage > 1) { routeChangeReportPage--; loadRouteChangeReport(); } });
$('#route-change-report-next').addEventListener('click', () => { if (routeChangeReportPage < routeChangeReportPageCount) { routeChangeReportPage++; loadRouteChangeReport(); } });
$('#route-change-report-body').addEventListener('click', event => {
  const button = event.target.closest('[data-route-change-invoice]');
  if (!button) return;
  const row = routeChangeReportRows.find(item => String(item.id) === button.dataset.routeChangeInvoice);
  if (!row?.invoice) return;
  const invoice = row.invoice;
  const amountRows = [
    ['إجمالي الرحلة قبل هذا الطلب', invoice.previousTotal],
    ['أجرة الجزء المكتمل', invoice.completedFare], ['رسوم الخدمة الأصلية', invoice.originalServiceFee],
    ['رسوم تغييرات سابقة', invoice.previousRouteChangeFees], ['رسوم هذا التغيير', invoice.routeChangeFee],
    ['أجرة المسار الجديد المتبقي', invoice.newRouteFare], ['رسوم الانتظار', invoice.waitingFee],
    ['الإجمالي الجديد', invoice.newTotal], ['الفرق عن الإجمالي السابق', invoice.difference]
  ];
  $('#route-change-invoice-title').textContent = `فاتورة تغيير المسار — رحلة #${row.rideId} · طلب #${row.id}`;
  $('#route-change-invoice-content').innerHTML = `<p class="admin-report-note">${row.status === 'accepted' ? 'الأجرة المعتمدة عند قبول هذا الطلب. لا تعني هذه الفاتورة تحصيل المبلغ.' : 'عرض أجرة محفوظ لهذا الطلب؛ لم تُعتمد هذه الرسوم على الراكب.'} القيم تخص وقت الطلب وقد تختلف عن إجمالي الرحلة الحالي.</p>
    <p><strong>الوجهة السابقة:</strong> ${escapeHtml(row.oldDestinationLabel || '—')}<br><strong>الوجهة الجديدة:</strong> ${escapeHtml(row.newDestinationLabel || '—')}</p>
    <dl class="route-change-invoice-amounts">${amountRows.map(([label, value]) => `<div><dt>${label}</dt><dd>${number(value)} ر.ي</dd></div>`).join('')}</dl>
    <p class="admin-report-note">المسافة المكتملة: ${number(Number(invoice.completedDistanceMeters) / 1000)} كم<br>المسافة الجديدة المتبقية: ${number(Number(invoice.newDistanceMeters) / 1000)} كم<br>مدة المسار الجديد: ${number(Math.ceil(Number(invoice.newDurationSeconds) / 60))} دقيقة</p>`;
  $('#route-change-invoice-dialog').showModal();
});
