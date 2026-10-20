(() => {
  const token = new URLSearchParams(window.location.search).get('token');
  const statusElement = document.getElementById('status');
  const messageElement = document.getElementById('message');
  const routeElement = document.getElementById('route');
  const map = L.map('map', { zoomControl: true }).setView([15.3694, 44.191], 13);
  L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
    maxZoom: 19,
    attribution: '&copy; OpenStreetMap contributors'
  }).addTo(map);

  let connection = null;
  let driverMarker = null;
  let endpointMarkers = [];
  let lastObservedAt = null;
  let animationFrame = 0;
  let retryTimer = null;
  let stopped = false;
  let routeLine = null;
  let snapshotRevision = 0;

  function decodeRoute(encoded) {
    if (typeof encoded !== 'string' || encoded.length > 500000) return [];
    let index = 0, latitude = 0, longitude = 0;
    const points = [];
    const component = () => {
      let value = 0, shift = 0, byte;
      do {
        if (index >= encoded.length || shift > 30) throw new Error('invalid route');
        byte = encoded.charCodeAt(index++) - 63;
        if (byte < 0 || byte > 63) throw new Error('invalid route');
        value |= (byte & 31) << shift; shift += 5;
      } while (byte >= 32);
      return value & 1 ? ~(value >> 1) : value >> 1;
    };
    try {
      while (index < encoded.length && points.length < 50000) {
        latitude += component(); longitude += component();
        if (!validPoint(latitude / 100000, longitude / 100000)) return [];
        points.push([latitude / 100000, longitude / 100000]);
      }
    } catch (_) { return []; }
    return points;
  }

  const statusNames = {
    0: 'مسودة', 1: 'يتم البحث عن سائق', 2: 'يجري التفاوض',
    3: 'تم تعيين السائق', 4: 'السائق في الطريق', 5: 'الرحلة جارية',
    6: 'اكتملت الرحلة', 7: 'أُلغيت الرحلة', 8: 'الإلغاء قيد المراجعة'
  };

  function setStatus(text) {
    statusElement.textContent = text;
  }

  function setMessage(text) {
    messageElement.textContent = text || '';
  }

  function validPoint(latitude, longitude) {
    return Number.isFinite(latitude) && Number.isFinite(longitude) &&
      latitude >= -90 && latitude <= 90 && longitude >= -180 && longitude <= 180;
  }

  function showRideStatus(status) {
    const numericStatus = Number(status);
    setStatus(statusNames[numericStatus] || 'الرحلة مشتركة');
    if (numericStatus === 6 || numericStatus === 7) {
      setMessage('انتهى التتبع لأن الرحلة اكتملت أو أُلغيت.');
      stopLiveConnection();
    }
  }

  function addEndpoint(label, latitude, longitude, color) {
    if (!validPoint(latitude, longitude)) return null;
    const marker = L.circleMarker([latitude, longitude], {
      radius: 8,
      color: '#fff',
      weight: 2,
      fillColor: color,
      fillOpacity: 1
    }).addTo(map).bindTooltip(label, { direction: 'top' });
    endpointMarkers.push(marker);
    return marker;
  }

  function updateDriverLocation(sample) {
    const latitude = Number(sample?.latitude);
    const longitude = Number(sample?.longitude);
    if (!validPoint(latitude, longitude)) return;
    const observedAt = sample.observedAtUtc ? Date.parse(sample.observedAtUtc) : NaN;
    if (Number.isFinite(observedAt) && lastObservedAt != null && observedAt <= lastObservedAt) return;
    if (Number.isFinite(observedAt)) lastObservedAt = observedAt;

    const target = L.latLng(latitude, longitude);
    if (!driverMarker) {
      driverMarker = L.marker(target, {
        icon: L.divIcon({
          className: '',
          html: '<div class="driver-marker"><span aria-hidden="true">➤</span></div>',
          iconSize: [42, 42],
          iconAnchor: [21, 21]
        }),
        zIndexOffset: 1000
      }).addTo(map).bindTooltip('موقع السائق');
    }

    if (animationFrame) cancelAnimationFrame(animationFrame);
    const start = driverMarker.getLatLng();
    const startedAt = performance.now();
    const duration = 1500;
    const step = now => {
      const progress = Math.min(1, (now - startedAt) / duration);
      driverMarker.setLatLng([
        start.lat + (target.lat - start.lat) * progress,
        start.lng + (target.lng - start.lng) * progress
      ]);
      const bearing = Number(sample.bearing);
      const pin = driverMarker.getElement()?.querySelector('.driver-marker span');
      if (pin && Number.isFinite(bearing)) pin.style.transform = `rotate(${bearing}deg)`;
      if (progress < 1) animationFrame = requestAnimationFrame(step);
      else animationFrame = 0;
    };
    animationFrame = requestAnimationFrame(step);
    setMessage(sample.observedAtUtc
      ? `آخر تحديث لموقع السائق: ${new Date(sample.observedAtUtc).toLocaleTimeString('ar-YE')}`
      : 'يصل الموقع مباشرة عند توفره.');
  }

  async function loadSnapshot() {
    const revision = ++snapshotRevision;
    if (!token) throw new Error('رابط المشاركة غير صالح.');
    const response = await fetch(`/api/safety/shares/${encodeURIComponent(token)}`, {
      cache: 'no-store',
      referrerPolicy: 'no-referrer'
    });
    if (!response.ok) throw new Error('رابط المشاركة غير متاح أو انتهت صلاحيته.');
    const snapshot = await response.json();
    if (revision !== snapshotRevision) return;
    showRideStatus(snapshot.status);
    if (routeLine) { routeLine.remove(); routeLine = null; }
    const routePoints = decodeRoute(snapshot.routePolyline);
    if (routePoints.length > 1) routeLine = L.polyline(routePoints, { color: '#147d5a', weight: 5 }).addTo(map);

    endpointMarkers.forEach(marker => marker.remove());
    endpointMarkers = [];
    const pickup = addEndpoint('نقطة الانطلاق', Number(snapshot.pickupLatitude), Number(snapshot.pickupLongitude), '#147d5a');
    const destination = addEndpoint('الوجهة', Number(snapshot.destinationLatitude), Number(snapshot.destinationLongitude), '#d08b22');
    updateDriverLocation(snapshot);

    const points = [pickup, destination, driverMarker].filter(Boolean).map(marker => marker.getLatLng());
    if (points.length > 1) map.fitBounds(L.latLngBounds(points).pad(0.18), { maxZoom: 15 });
    else if (points.length === 1) map.setView(points[0], 15);

    const pickupName = snapshot.pickup || 'نقطة الانطلاق';
    const destinationName = snapshot.destination || 'الوجهة';
    routeElement.textContent = `${pickupName} ← ${destinationName}${snapshot.driverName ? ` · السائق: ${snapshot.driverName}` : ''}`;
    if (!driverMarker) setMessage('لا توجد عينة GPS حديثة للسائق حتى الآن.');
  }

  function stopLiveConnection() {
    stopped = true;
    clearTimeout(retryTimer);
    retryTimer = null;
    if (connection) connection.stop().catch(() => {});
    connection = null;
  }

  async function startLiveConnection() {
    if (stopped || !token || connection) return;
    const current = new signalR.HubConnectionBuilder()
      .withUrl(`/hubs/shared-rides?token=${encodeURIComponent(token)}`)
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();
    connection = current;
    current.on('sharedRideLocationUpdated', updateDriverLocation);
    current.on('sharedRideStatusUpdated', async payload => {
      showRideStatus(payload?.rideStatus);
      if (stopped) return;
      try { await loadSnapshot(); } catch (error) { setMessage(error.message); }
    });
    current.on('sharedRideRevoked', () => {
      setStatus('تم إيقاف مشاركة الرحلة');
      setMessage('ألغى صاحب الرحلة رابط المشاركة.');
      stopLiveConnection();
    });
    current.onreconnecting(() => setStatus('يعاد الاتصال بالخريطة…'));
    current.onreconnected(async () => {
      try {
        await loadSnapshot();
        setStatus('تتبع مباشر للرحلة');
      } catch (error) {
        setMessage(error.message);
        stopLiveConnection();
      }
    });
    current.onclose(() => {
      if (stopped || connection !== current) return;
      connection = null;
      setStatus('انقطع الاتصال المباشر');
      retryTimer = setTimeout(() => startLiveConnection(), 5000);
    });

    try {
      await current.start();
      if (connection !== current || stopped) {
        await current.stop();
        return;
      }
      setStatus('تتبع مباشر للرحلة');
    } catch (_) {
      if (connection === current) connection = null;
      setStatus('تعذر الاتصال المباشر');
      retryTimer = setTimeout(() => startLiveConnection(), 5000);
    }
  }

  if (!token || !window.signalR || !window.L) {
    setStatus('رابط المشاركة غير صالح');
    setMessage('تعذر فتح خريطة الرحلة المشتركة.');
    routeElement.textContent = '';
    stopLiveConnection();
    return;
  }

  loadSnapshot()
    .then(() => startLiveConnection())
    .catch(error => {
      setStatus('تعذر فتح الرحلة المشتركة');
      setMessage(error.message);
      routeElement.textContent = '';
    });
})();
