const driverLocationEditor = { generation: 0, driverId: null, map: null, marker: null,
  route: null, busy: false, snapshot: null, draft: null, dirty: false };
const driverMovementReport = { generation: 0, initialized: false, page: 1, pageCount: 1, filters: null };
const rideMovementMap = { generation: 0, map: null, rideId: null };
let rideMovementComplaintGeneration = 0;

function movementDate(value) {
  const parsed = value ? new Date(value) : null;
  return parsed && Number.isFinite(parsed.getTime()) ? new Intl.DateTimeFormat('ar-YE-u-nu-latn', {
    dateStyle: 'short', timeStyle: 'medium', timeZone: 'Asia/Aden'
  }).format(parsed) : 'غير مسجل';
}
function movementDuration(value) {
  if (value == null || !Number.isFinite(Number(value)) || Number(value) < 0) return 'غير مسجلة';
  const seconds = Math.floor(Number(value)), hours = Math.floor(seconds / 3600);
  return `${hours ? `${number(hours)} س ` : ''}${number(Math.floor(seconds % 3600 / 60))} د ${number(seconds % 60)} ث`;
}
function movementPoint(value) { return value ? validMapPoint(value.latitude, value.longitude) : null; }
function movementFeedback(selector, message, error = false) {
  const element = $(selector); element.textContent = message; element.classList.toggle('error', error);
}
function updateDriverLocationControls() {
  const editor = driverLocationEditor, writable = canAdmin('personal_data.write');
  $('#driver-location-save').disabled = editor.busy || !writable || !editor.draft || !editor.dirty;
  $('#driver-location-move').disabled = editor.busy || !writable || editor.dirty || !editor.snapshot?.canMove;
  $('#driver-location-reload').disabled = editor.busy;
  for (const selector of ['#driver-location-latitude', '#driver-location-longitude']) $(selector).disabled = editor.busy || !writable;
  if (editor.marker?.dragging) editor.busy || !writable ? editor.marker.dragging.disable() : editor.marker.dragging.enable();
}
function setDriverLocationDraft(point, dirty = true) {
  const editor = driverLocationEditor;
  if (!point || !editor.map) return;
  editor.draft = point;
  editor.dirty = dirty;
  $('#driver-location-latitude').value = String(point.lat);
  $('#driver-location-longitude').value = String(point.lon);
  if (editor.marker) editor.marker.setLatLng([point.lat, point.lon]);
  else {
    editor.marker = L.marker([point.lat, point.lon], { draggable: canAdmin('personal_data.write'),
      icon: AdminMaps.vehicleIcon(Number(editor.snapshot?.ride?.status) === 5, editor.snapshot?.location?.bearing), title: 'موقع السائق المختار' }).addTo(editor.map);
    editor.marker.on('dragend', () => {
      if (editor.busy) return;
      const position = editor.marker.getLatLng();
      setDriverLocationDraft(validMapPoint(position.lat, position.lng));
      movementFeedback('#driver-location-feedback', 'موقع غير محفوظ. اضغط حفظ الموقع لتحديث التتبع.');
    });
  }
  updateDriverLocationControls();
}
function showDriverLocationSnapshot(snapshot) {
  const editor = driverLocationEditor;
  editor.snapshot = snapshot;
  const point = movementPoint(snapshot.location);
  editor.draft = null; editor.dirty = false;
  if (editor.marker) { editor.map.removeLayer(editor.marker); editor.marker = null; }
  if (point) { setDriverLocationDraft(point, false); editor.map.setView([point.lat, point.lon], 17); }
  else { $('#driver-location-latitude').value = ''; $('#driver-location-longitude').value = ''; }
  if (editor.route) { editor.map.removeLayer(editor.route); editor.route = null; }
  const route = AdminMaps.decodePolyline(snapshot.routePolyline);
  if (route.length) editor.route = L.polyline(route, { color: '#2563eb', weight: 4, opacity: .65, dashArray: '8 6' }).addTo(editor.map);
  $('#driver-location-details').textContent = `${point ? `آخر موقع محفوظ: ${movementDate(snapshot.location.observedAtUtc)}` : 'لا يوجد موقع محفوظ للسائق.'}${snapshot.ride ? ` · رحلة #${snapshot.ride.id} · ${rideStatuses[snapshot.ride.status] || 'حالة غير معروفة'}` : ' · لا توجد رحلة جارية.'}`;
  $('#driver-location-move-note').textContent = snapshot.canMove
    ? 'تجربة التحرك تقدّم الموقع مترًا واحدًا على مسار الرحلة الحالي وتحدّث التتبع مباشرة. احفظ أي تعديل يدوي أولًا.'
    : 'تجربة التحرك متاحة فقط عندما يكون السائق متجهًا إلى الراكب أو ينفذ الرحلة.';
  if (snapshot.routeError) $('#driver-location-move-note').textContent += ` ${snapshot.routeError}`;
  updateDriverLocationControls();
}
async function reloadDriverLocation() {
  const editor = driverLocationEditor;
  if (editor.busy || !editor.driverId) return;
  const generation = editor.generation;
  editor.busy = true; updateDriverLocationControls();
  movementFeedback('#driver-location-feedback', 'جارٍ تحميل موقع السائق…');
  try {
    const result = await execute('DriverLocationAdminModel', 'get', { driverId: editor.driverId });
    if (generation !== editor.generation) return;
    showDriverLocationSnapshot(result.data);
    movementFeedback('#driver-location-feedback', 'انقر على الخريطة أو اسحب العلامة لاختيار الموقع، ثم اضغط حفظ الموقع.');
  } catch (error) {
    if (generation === editor.generation) movementFeedback('#driver-location-feedback', error.message, true);
  } finally { if (generation === editor.generation) { editor.busy = false; updateDriverLocationControls(); } }
}
window.openDriverLocation = async driverId => {
  if (!canAdmin('personal_data.read')) return toast('عرض موقع السائق يتطلب صلاحية قراءة بيانات المستخدمين.', true);
  closeDriverLocation();
  const editor = driverLocationEditor;
  editor.driverId = Number(driverId);
  if (!Number.isSafeInteger(editor.driverId) || editor.driverId <= 0) return;
  const driver = state.drivers.find(item => Number(item.userId) === editor.driverId);
  $('#driver-location-title').textContent = `موقع السائق · ${driver?.displayName || `#${editor.driverId}`}`;
  $('#driver-location-details').textContent = '';
  $('#driver-location-move-note').textContent = '';
  $('#driver-location-latitude').value = ''; $('#driver-location-longitude').value = '';
  $('#driver-location-dialog').showModal();
  if (!window.L) { movementFeedback('#driver-location-feedback', 'تعذر تحميل أدوات الخريطة. أعد تحميل الصفحة.', true); return; }
  editor.map = L.map('driver-location-map').setView([15.3694, 44.1910], 12);
  AdminMaps.attachBaseLayer(editor.map, $('#driver-location-map-status'), $('#driver-location-map-retry'));
  editor.map.on('click', event => {
    if (editor.busy || !canAdmin('personal_data.write')) return;
    setDriverLocationDraft(validMapPoint(event.latlng.lat, event.latlng.lng));
    movementFeedback('#driver-location-feedback', 'موقع غير محفوظ. اضغط حفظ الموقع لتحديث التتبع.');
  });
  const map = editor.map;
  setTimeout(() => { if (editor.map === map) map.invalidateSize(); }, 0);
  await reloadDriverLocation();
};
function closeDriverLocation() {
  const editor = driverLocationEditor;
  editor.generation++; editor.map?.remove(); editor.map = null; editor.marker = null; editor.route = null;
  editor.driverId = null; editor.snapshot = null; editor.draft = null; editor.dirty = false; editor.busy = false;
}
async function writeDriverLocation(operation) {
  const editor = driverLocationEditor;
  if (editor.busy || !editor.driverId || !canAdmin('personal_data.write')) return;
  const point = validMapPoint($('#driver-location-latitude').value, $('#driver-location-longitude').value);
  if (operation === 'save' && !point) return movementFeedback('#driver-location-feedback', 'أدخل خط عرض بين ‎-90 و90 وخط طول بين ‎-180 و180.', true);
  if (operation === 'move' && (editor.dirty || !editor.snapshot?.canMove)) return;
  const generation = editor.generation, driverId = editor.driverId;
  editor.busy = true; updateDriverLocationControls();
  movementFeedback('#driver-location-feedback', operation === 'save' ? 'جارٍ حفظ الموقع…' : 'جارٍ تحريك السائق مترًا على المسار…');
  try {
    const result = await execute('DriverLocationAdminModel', operation, { driverId,
      ...(operation === 'save' ? { latitude: point.lat, longitude: point.lon } : {}) }, { confirmed: true });
    if (generation !== editor.generation) return;
    showDriverLocationSnapshot(result.data);
    const snap = Number(result.data.snapDistanceMeters || 0);
    movementFeedback('#driver-location-feedback', operation === 'save' ? 'تم حفظ موقع السائق وتحديث التتبع.' : `${snap >= 1 ? `تمت مواءمة الموقع مع المسار بمقدار ${number(snap)} متر، ثم ` : 'تم '}التحرك ${number(result.data.distanceMeters ?? 1)} متر على المسار وتحديث التتبع.`);
  } catch (error) {
    if (generation === editor.generation) movementFeedback('#driver-location-feedback', error.message || 'تعذر تحديث الموقع. أعد تحميل الموقع للتحقق قبل المحاولة.', true);
  } finally { if (generation === editor.generation) { editor.busy = false; updateDriverLocationControls(); } }
}

function driverMovementFilters() {
  const driverId = Number($('#driver-movement-driver').value), from = $('#driver-movement-from').value, to = $('#driver-movement-to').value;
  if (!Number.isSafeInteger(driverId) || driverId <= 0) throw new Error('اختر السائق أولًا.');
  if (from && to && from > to) throw new Error('تاريخ البداية يجب أن يسبق تاريخ النهاية أو يساويه.');
  return { driverId, fromUtc: from ? new Date(`${from}T00:00:00+03:00`).toISOString() : null,
    toUtc: to ? new Date(new Date(`${to}T00:00:00+03:00`).getTime() + 86400000).toISOString() : null };
}
async function loadDriverMovementReport(refresh = false) {
  if (!canAdmin('operations.read')) { movementFeedback('#driver-movement-feedback', 'التقرير يتطلب صلاحية متابعة العمليات.', true); return; }
  if (driverMovementReport.initialized && !refresh) return;
  const generation = ++driverMovementReport.generation;
  $('#driver-movement-driver').disabled = true;
  movementFeedback('#driver-movement-feedback', 'جارٍ تحميل قائمة السائقين…');
  try {
    const selected = $('#driver-movement-driver').value;
    const result = await execute('DriverMovementReportModel', 'search', { search: $('#driver-movement-search').value.trim() || null });
    if (generation !== driverMovementReport.generation) return;
    const drivers = result.data || [];
    $('#driver-movement-driver').innerHTML = '<option value="">اختر السائق</option>' + drivers.map(row => `<option value="${Number(row.id)}">${escapeHtml(row.displayName || 'بدون اسم')} · ${escapeHtml(row.phoneNumber || '')}</option>`).join('');
    $('#driver-movement-driver').value = selected;
    driverMovementReport.initialized = true;
    movementFeedback('#driver-movement-feedback', drivers.length ? 'اختر السائق والفترة ثم اضغط عرض التقرير. استخدم البحث بالاسم أو الهاتف لتضييق قائمة السائقين.' : 'لا يوجد سائقون يطابقون البحث.');
  } catch (error) { if (generation === driverMovementReport.generation) movementFeedback('#driver-movement-feedback', error.message, true); }
  finally { if (generation === driverMovementReport.generation) $('#driver-movement-driver').disabled = false; }
}
function driverMovementReportRow(row) {
  const id = Number(row.rideId || row.id);
  const tripDelay = row.tripDelayAssessed
    ? Number(row.tripDelaySeconds) > 0 ? `تجاوز المدة المقدرة: ${movementDuration(row.tripDelaySeconds)}` : 'ضمن المدة المقدرة'
    : 'تأخر الرحلة: غير قابل للتقييم لغياب مدة مقارنة مكتملة وثابتة.';
  return `<tr><td><span class="cell-main">رحلة #${id}</span><span class="cell-sub">${escapeHtml(row.customerName || '—')}</span><span class="cell-sub">${escapeHtml(movementDate(row.createdAtUtc))}</span><span class="cell-sub">${escapeHtml(row.pickup || 'الانطلاق')} ← ${escapeHtml(row.destination || 'الوجهة')}</span></td>
    <td>${rideBadge(row.status)}<span class="cell-sub">الاتفاق: ${escapeHtml(movementDate(row.acceptedAtUtc))}</span></td>
    <td>${row.ratingScore == null ? 'لم تُقيّم' : `<span class="cell-main">${number(row.ratingScore)} / 5</span><span class="cell-sub">${escapeHtml(row.ratingComment || '')}</span>`}</td>
    <td><span class="cell-main">${movementDuration(row.arrivalDurationSeconds)}</span>${row.arrivalElapsedSeconds != null ? `<span class="cell-sub">المدة المنقضية دون وصول مسجل: ${movementDuration(row.arrivalElapsedSeconds)}</span>` : ''}<span class="cell-sub">الوصول: ${escapeHtml(movementDate(row.driverArrivedAtUtc))}</span>${row.arrivalDelayNote ? `<span class="cell-sub">${escapeHtml(row.arrivalDelayNote)}</span>` : ''}</td>
    <td><span class="cell-main">${movementDuration(row.tripDurationSeconds)}</span>${row.startedAtUtc && !row.serviceEndedAtUtc && [3, 4, 5, 8, 9].includes(Number(row.status)) ? '<span class="cell-sub">المدة حتى تحديث التقرير</span>' : ''}<span class="cell-sub">المدة المقدرة: ${movementDuration(row.plannedTripDurationSeconds)}</span><span class="cell-sub">${escapeHtml(tripDelay)}</span><span class="cell-sub">البدء: ${escapeHtml(movementDate(row.startedAtUtc))}</span><span class="cell-sub">النهاية: ${escapeHtml(movementDate(row.serviceEndedAtUtc))}</span></td>
    <td>${row.routeChanged ? badge(`${number(row.routeChangeCount)} تغيير معتمد`, 'warning') : 'لا يوجد تغيير معتمد'}</td>
    <td><span class="cell-main">${number(row.delayCount || 0)} توقف / انتظار مسجل</span>${Number(row.delayCount) > 0 ? `<span class="cell-sub">مدة الانتظار المنتهي: ${movementDuration(row.delayDurationSeconds)}</span>` : ''}${Number(row.activeDelayCount) > 0 ? `<span class="cell-sub">${number(row.activeDelayCount)} انتظار جارٍ</span>` : ''}</td>
    <td>${Number(row.complaintCount) > 0 ? `<button type="button" class="button" data-movement-complaints="${id}">عرض ${number(row.complaintCount)} بلاغ</button><span class="cell-sub">${number(row.openComplaintCount || 0)} مفتوح</span>` : 'لا توجد بلاغات'}<button type="button" class="button" data-movement-map="${id}">عرض مسارات الرحلة</button></td></tr>`;
}
async function showDriverMovementReport() {
  if (!driverMovementReport.filters) return;
  const generation = ++driverMovementReport.generation;
  $('#driver-movement-prev').disabled = true; $('#driver-movement-next').disabled = true;
  $('#view-driver-movement').setAttribute('aria-busy', 'true');
  movementFeedback('#driver-movement-feedback', 'جارٍ تحميل التقرير…');
  try {
    const result = await execute('DriverMovementReportModel', 'list', { ...driverMovementReport.filters, page: driverMovementReport.page, pageSize: 25 });
    if (generation !== driverMovementReport.generation) return;
    const report = result.data, rows = report.rows || [];
    driverMovementReport.page = report.page; driverMovementReport.pageCount = report.pageCount;
    toggleEmpty('#driver-movement-body', '#driver-movement-empty', rows.map(driverMovementReportRow));
    $('#driver-movement-page').textContent = `صفحة ${number(report.page)} من ${number(report.pageCount)} · ${number(report.totalCount)} رحلة`;
    $('#driver-movement-prev').disabled = report.page <= 1; $('#driver-movement-next').disabled = report.page >= report.pageCount;
    movementFeedback('#driver-movement-feedback', `الرحلات التي تم الاتفاق عليها مع السائق في الفترة المحددة. آخر تحديث: ${movementDate(report.serverNowUtc)}`);
  } catch (error) {
    if (generation !== driverMovementReport.generation) return;
    $('#driver-movement-body').innerHTML = ''; $('#driver-movement-page').textContent = ''; $('#driver-movement-empty').classList.add('hidden');
    movementFeedback('#driver-movement-feedback', error.message, true);
  } finally { if (generation === driverMovementReport.generation) $('#view-driver-movement').removeAttribute('aria-busy'); }
}

function movementHistorySegments(samples) {
  const segments = []; let segment = [], previousTime = null;
  const flush = () => { if (segment.length) segments.push(segment); segment = []; previousTime = null; };
  for (const sample of samples || []) {
    const point = movementPoint(sample), time = sample.observedAtUtc ? new Date(sample.observedAtUtc).getTime() : NaN;
    if (!point) { flush(); continue; }
    if (previousTime != null && (!Number.isFinite(time) || time < previousTime || time - previousTime > 120000)) flush();
    segment.push([point.lat, point.lon]); previousTime = Number.isFinite(time) ? time : null;
  }
  flush(); return segments;
}
function renderRideMovementHistory(history) {
  const map = rideMovementMap.map, bounds = [];
  const notes = [...(history.notes || [])];
  const archivedSegments = (samples, saved) => Array.isArray(saved) ? saved.flatMap(movementHistorySegments) : movementHistorySegments(samples);
  const tripSegments = archivedSegments(history.tripSamples, history.tripSegments);
  const approachSegments = archivedSegments(history.approachSamples, history.approachSegments);
  const marker = (value, label, color, radius = 8) => {
    const point = movementPoint(value); if (!point) { notes.push(`${label}: غير مسجل.`); return; }
    const position = [point.lat, point.lon]; bounds.push(position);
    const layer = label === 'نقطة الانطلاق' || label === 'الوجهة'
      ? L.marker(position, { icon: AdminMaps.stopIcon(label === 'الوجهة') })
      : L.circleMarker(position, { radius, color: '#fff', weight: 2, fillColor: color, fillOpacity: 1 });
    layer.addTo(map)
      .bindTooltip(escapeHtml(label), { direction: 'top' }).bindPopup(`${escapeHtml(label)}${value.observedAtUtc ? `<br>${escapeHtml(movementDate(value.observedAtUtc))}` : ''}`);
  };
  const requestPoint = movementPoint(history.driverAtRequest), lastRecorded = (tripSegments.length ? tripSegments : approachSegments).at(-1)?.at(-1);
  if (!requestPoint || lastRecorded && (Math.abs(lastRecorded[0] - requestPoint.lat) > .000005 || Math.abs(lastRecorded[1] - requestPoint.lon) > .000005))
    marker(history.driverAtRequest, history.driverAtRequest?.source === 'request_invitation' ? 'السائق عند عرض الطلب عليه' : 'السائق عند الطلب', '#b45309');
  marker(history.passengerAtRequest, 'الراكب عند الطلب', '#9333ea', 12);
  marker(history.pickup, 'نقطة الانطلاق', '#08766a', 6);
  marker(history.destination, 'الوجهة', '#dc2626');
  const planned = AdminMaps.decodePolyline(history.plannedRoutePolyline);
  if (planned.length) {
    bounds.push(...planned);
    L.polyline(planned, { color: '#64748b', weight: 3, opacity: .6, dashArray: '8 7' }).addTo(map).bindTooltip('المسار المخطط للرحلة');
  } else notes.push('المسار المخطط للرحلة غير محفوظ.');
  for (const change of history.routeChanges || []) {
    const point = validMapPoint(change.changeLatitude, change.changeLongitude);
    if (!point) continue;
    const position = [point.lat, point.lon]; bounds.push(position);
    L.circleMarker(position, { radius: 7, color: '#c2410c', weight: 2, fillColor: '#fed7aa', fillOpacity: 1 }).addTo(map)
      .bindTooltip(`تغيير وجهة #${Number(change.id)}`)
      .bindPopup(`تغيير وجهة #${Number(change.id)}<br>${escapeHtml(change.oldDestinationLabel || 'الوجهة السابقة')} ← ${escapeHtml(change.newDestinationLabel || 'الوجهة الجديدة')}<br>${escapeHtml(movementDate(change.decidedAtUtc || change.observedAtUtc))}`);
  }
  for (const [samples, savedSegments, color, label] of [[history.approachSamples, history.approachSegments, '#2563eb', 'حركة السائق إلى الراكب'], [history.tripSamples, history.tripSegments, '#08766a', 'حركة الرحلة إلى الوجهة']]) {
    const segments = Array.isArray(savedSegments) ? savedSegments.flatMap(movementHistorySegments) : movementHistorySegments(samples);
    if (!segments.length) notes.push(`${label}: لا توجد نقاط حركة محفوظة.`);
    for (const segment of segments) {
      bounds.push(...segment);
      if (segment.length >= 2) L.polyline(segment, { color, weight: 5, opacity: .85 }).addTo(map).bindTooltip(label);
      else L.circleMarker(segment[0], { radius: 4, color, fillOpacity: .8 }).addTo(map).bindTooltip(`${label}: نقطة محفوظة منفردة`);
    }
  }
  // The vehicle marks the latest recorded fix; never connect separate archived
  // segments to invent a direction across a manual relocation or missing GPS.
  const inProgress = tripSegments.length > 0;
  const lastSegment = (inProgress ? tripSegments : approachSegments).at(-1);
  const current = lastSegment?.at(-1) || (movementPoint(history.driverAtRequest) && [history.driverAtRequest.latitude, history.driverAtRequest.longitude]);
  if (current) {
    const previous = lastSegment?.at(-2), from = previous && { lat: previous[0], lon: previous[1] }, to = { lat: current[0], lon: current[1] };
    const bearing = AdminMaps.movementBearing(from, to, null);
    L.marker(current, { icon: AdminMaps.vehicleIcon(inProgress, bearing) }).addTo(map)
      .bindPopup(inProgress ? 'آخر موقع محفوظ للمركبة أثناء الرحلة' : 'آخر موقع محفوظ للسائق قبل الانطلاق');
    bounds.push(current);
  }
  if (bounds.length) map.fitBounds(bounds, { padding: [35, 35], maxZoom: 17 });
  $('#ride-movement-details').textContent = `رحلة #${history.rideId} · ${rideStatuses[history.status] || '—'} · الطلب: ${movementDate(history.requestedAtUtc)} · الاتفاق: ${movementDate(history.acceptedAtUtc)}`;
  $('#ride-movement-notes').innerHTML = [...new Set(notes)].map(note => `<p class="movement-report-note">${escapeHtml(note)}</p>`).join('');
  movementFeedback('#ride-movement-feedback', 'الخطوط الملونة تصل نقاط الحركة المسجلة. الخط الرمادي المتقطع هو المسار المخطط ولا يثبت أن السائق سلكه. الفجوات الكبيرة منفصلة، وقد لا يغطي السجل كامل الحركة.');
}
window.openRideMovementMap = async rideId => {
  closeRideMovementMap();
  const generation = rideMovementMap.generation;
  rideMovementMap.rideId = Number(rideId);
  $('#ride-movement-title').textContent = `مسارات الرحلة #${Number(rideId)}`;
  $('#ride-movement-details').textContent = ''; $('#ride-movement-notes').innerHTML = '';
  $('#ride-movement-dialog').showModal();
  movementFeedback('#ride-movement-feedback', 'جارٍ تحميل المسارات المسجلة…');
  if (!window.L) { movementFeedback('#ride-movement-feedback', 'تعذر تحميل أدوات الخريطة. أعد تحميل الصفحة.', true); return; }
  rideMovementMap.map = L.map('ride-movement-map').setView([15.3694, 44.1910], 12);
  AdminMaps.attachBaseLayer(rideMovementMap.map, $('#ride-movement-map-status'), $('#ride-movement-map-retry'));
  const map = rideMovementMap.map;
  setTimeout(() => { if (rideMovementMap.map === map) map.invalidateSize(); }, 0);
  try {
    const result = await execute('RideMovementHistoryModel', 'get', { rideId: Number(rideId) });
    if (generation === rideMovementMap.generation) renderRideMovementHistory(result.data);
  } catch (error) { if (generation === rideMovementMap.generation) movementFeedback('#ride-movement-feedback', error.message, true); }
};
function closeRideMovementMap() { rideMovementMap.generation++; rideMovementMap.map?.remove(); rideMovementMap.map = null; rideMovementMap.rideId = null; }
function movementComplaintCard(item, safety) {
  const status = safety ? safetyStatuses[item.status] || '—' : reviewStatus(item.status);
  return `<article class="movement-complaint"><header><strong>${safety ? 'بلاغ سلامة' : 'بلاغ دعم'} #${Number(item.id)}</strong>${badge(status)}</header><p>${escapeHtml(item.message || '')}</p><span class="cell-sub">${escapeHtml(movementDate(item.createdAtUtc))}</span>${item.adminReply || item.adminNote ? `<p>رد الإدارة: ${escapeHtml(item.adminReply || item.adminNote)}</p>` : ''}</article>`;
}
window.openRideMovementComplaints = async rideId => {
  const generation = ++rideMovementComplaintGeneration;
  $('#ride-complaints-title').textContent = `بلاغات الرحلة #${Number(rideId)}`;
  $('#ride-complaints-support').innerHTML = ''; $('#ride-complaints-safety').innerHTML = '';
  $('#ride-complaints-dialog').showModal();
  movementFeedback('#ride-complaints-feedback', 'جارٍ تحميل بلاغات هذه الرحلة…');
  const requests = [['SupportTicketModel', 'support.read', '#ride-complaints-support', false], ['SafetyAdminReportModel', 'safety.read', '#ride-complaints-safety', true]];
  const results = await Promise.allSettled(requests.map(([model, permission]) => canAdmin(permission)
    ? execute(model, 'list', { rideId: Number(rideId) }) : Promise.reject(new Error('لا تتوفر صلاحية عرض هذا القسم.'))));
  if (generation !== rideMovementComplaintGeneration) return;
  for (let index = 0; index < requests.length; index++) {
    const [, , selector, safety] = requests[index], result = results[index];
    if (result.status === 'rejected') $(selector).textContent = result.reason.message || 'تعذر تحميل البلاغات.';
    else {
      const rows = (result.value.data || []).filter(row => Number(row.rideId) === Number(rideId));
      $(selector).innerHTML = rows.map(row => movementComplaintCard(row, safety)).join('') || '<p class="movement-report-note">لا توجد بلاغات في هذا القسم.</p>';
    }
  }
  movementFeedback('#ride-complaints-feedback', 'البلاغات مرتبطة بهذه الرحلة فقط. تظهر أقسام الدعم والسلامة حسب صلاحيات حسابك.');
};
function resetDriverMovement() {
  driverMovementReport.generation++; driverMovementReport.initialized = false; driverMovementReport.filters = null;
  $('#driver-movement-driver').innerHTML = '<option value="">اختر السائق</option>';
  $('#driver-movement-body').innerHTML = ''; $('#driver-movement-page').textContent = '';
  $('#driver-movement-search').value = '';
  closeDriverLocation(); closeRideMovementMap(); rideMovementComplaintGeneration++;
  for (const selector of ['#driver-location-dialog', '#ride-movement-dialog', '#ride-complaints-dialog']) $(selector).close();
  $('#ride-complaints-support').innerHTML = ''; $('#ride-complaints-safety').innerHTML = ''; $('#ride-movement-notes').innerHTML = '';
  for (const selector of ['#driver-location-details', '#ride-movement-details', '#driver-location-feedback', '#ride-movement-feedback']) $(selector).textContent = '';
  $('#driver-location-latitude').value = ''; $('#driver-location-longitude').value = '';
}
$('#driver-location-dialog').addEventListener('close', closeDriverLocation);
$('#ride-movement-dialog').addEventListener('close', closeRideMovementMap);
$('#ride-complaints-dialog').addEventListener('close', () => { rideMovementComplaintGeneration++; });
$('#driver-location-reload').addEventListener('click', reloadDriverLocation);
$('#driver-location-save').addEventListener('click', () => writeDriverLocation('save'));
$('#driver-location-move').addEventListener('click', () => writeDriverLocation('move'));
for (const selector of ['#driver-location-latitude', '#driver-location-longitude']) $(selector).addEventListener('input', () => {
  if (driverLocationEditor.busy) return;
  const point = validMapPoint($('#driver-location-latitude').value, $('#driver-location-longitude').value);
  driverLocationEditor.dirty = true; driverLocationEditor.draft = point;
  if (point) setDriverLocationDraft(point);
  updateDriverLocationControls();
  movementFeedback('#driver-location-feedback', point ? 'موقع غير محفوظ. اضغط حفظ الموقع لتحديث التتبع.' : 'أكمل إحداثيات الموقع الصحيحة.', !point);
});
$('#driver-movement-filters').addEventListener('submit', event => {
  event.preventDefault();
  try { driverMovementReport.filters = driverMovementFilters(); driverMovementReport.page = 1; showDriverMovementReport(); }
  catch (error) { movementFeedback('#driver-movement-feedback', error.message, true); }
});
$('#driver-movement-refresh').addEventListener('click', () => driverMovementReport.filters ? showDriverMovementReport() : loadDriverMovementReport(true));
$('#driver-movement-search-button').addEventListener('click', () => loadDriverMovementReport(true));
$('#driver-movement-prev').addEventListener('click', () => { if (driverMovementReport.page > 1) { driverMovementReport.page--; showDriverMovementReport(); } });
$('#driver-movement-next').addEventListener('click', () => { if (driverMovementReport.page < driverMovementReport.pageCount) { driverMovementReport.page++; showDriverMovementReport(); } });
document.addEventListener('click', event => {
  const map = event.target.closest('[data-movement-map]'), complaints = event.target.closest('[data-movement-complaints]');
  if (map) openRideMovementMap(Number(map.dataset.movementMap));
  if (complaints) openRideMovementComplaints(Number(complaints.dataset.movementComplaints));
});
