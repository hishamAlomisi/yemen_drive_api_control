(function (root) {
  'use strict';

  const providers = [
    {
      url: 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Street_Map/MapServer/tile/{z}/{y}/{x}',
      attribution: 'Tiles &copy; <a href="https://www.esri.com/">Esri</a> — Sources: Esri, HERE, Garmin, USGS, Intermap, INCREMENT P, NRCan, Esri Japan, METI, Esri China (Hong Kong), Esri Korea, Esri (Thailand), NGCC, &copy; OpenStreetMap contributors, GIS User Community'
    },
    {
      url: 'https://{s}.tile.openstreetmap.fr/hot/{z}/{x}/{y}.png',
      attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors, <a href="https://www.hotosm.org/">HOT</a>, tiles <a href="https://www.openstreetmap.fr/">OpenStreetMap France</a>'
    }
  ];

  // Tile requests can fail independently of both Leaflet and the live feed.
  // Switch once per provider, and keep an explicit retry when both are offline.
  function attachBaseLayer(map, status, retry, leaflet = root.L) {
    let layer, timer, providerIndex = 0, disposed = false;
    const showStatus = (message, failed = false) => {
      if (status) { status.textContent = message; status.classList.toggle('hidden', !message); }
      if (retry) retry.classList.toggle('hidden', !failed);
    };
    const start = index => {
      if (disposed) return;
      clearTimeout(timer);
      if (layer) { layer.off(); map.removeLayer(layer); }
      providerIndex = index;
      let loaded = 0, errors = 0, finished = false;
      const current = leaflet.tileLayer(providers[index].url, {
        maxZoom: 19, attribution: providers[index].attribution,
        referrerPolicy: 'strict-origin-when-cross-origin'
      });
      layer = current;
      const fail = () => {
        if (disposed || layer !== current || finished) return;
        finished = true;
        clearTimeout(timer);
        if (index + 1 < providers.length) start(index + 1);
        else showStatus('تعذر تحميل خلفية الخريطة. تحقق من الاتصال ثم أعد المحاولة؛ مواقع الرحلات تبقى ظاهرة.', true);
      };
      current.on('loading', () => {
        loaded = 0; errors = 0; finished = false;
        showStatus('جارٍ تحميل الخريطة…');
        clearTimeout(timer);
        timer = setTimeout(fail, 8000);
      });
      current.on('tileload', () => {
        if (layer !== current || disposed) return;
        loaded++;
      });
      current.on('tileerror', () => {
        if (layer !== current || disposed) return;
        errors++;
        if (!loaded && errors >= 3) fail();
      });
      current.on('load', () => {
        if (layer !== current || disposed) return;
        clearTimeout(timer);
        if (errors) fail();
        else { finished = true; showStatus(''); }
      });
      showStatus(index ? 'جارٍ تحميل الخريطة من المصدر البديل…' : 'جارٍ تحميل الخريطة…');
      timer = setTimeout(fail, 8000);
      current.addTo(map);
    };
    const restart = () => start(0);
    retry?.addEventListener('click', restart);
    map.on('unload', () => {
      disposed = true;
      clearTimeout(timer);
      layer?.off();
      retry?.removeEventListener('click', restart);
    });
    start(0);
    return { retry: restart, get providerIndex() { return providerIndex; } };
  }

  function decodePolyline(encoded) {
    if (typeof encoded !== 'string' || !encoded || encoded.length > 200000) return [];
    let index = 0, lat = 0, lon = 0;
    const points = [];
    const delta = () => {
      let result = 0, shift = 0, byte;
      do {
        if (index >= encoded.length || shift > 30) throw new Error('Invalid polyline');
        byte = encoded.charCodeAt(index++) - 63;
        if (byte < 0 || byte > 63) throw new Error('Invalid polyline');
        result |= (byte & 31) << shift;
        shift += 5;
      } while (byte >= 32);
      return result & 1 ? ~(result >> 1) : result >> 1;
    };
    try {
      while (index < encoded.length) {
        lat += delta(); lon += delta();
        if (Math.abs(lat / 1e5) > 90 || Math.abs(lon / 1e5) > 180) return [];
        points.push([lat / 1e5, lon / 1e5]);
      }
    } catch (_) { return []; }
    return points.length >= 2 ? points : [];
  }

  function directionsUrl(origin, destination) {
    if (!origin || !destination) return '';
    const params = new URLSearchParams({ api: '1', origin: `${origin.lat},${origin.lon}`,
      destination: `${destination.lat},${destination.lon}`, travelmode: 'driving' });
    return `https://www.google.com/maps/dir/?${params}`;
  }

  const normalizedBearing = value => value != null && value !== '' && Number.isFinite(Number(value)) && Number(value) >= 0
    ? Number(value) % 360 : null;
  const bearingDelta = (from, to) => ((to - from + 540) % 360) - 180;

  function vehicleIcon(inProgress = false, bearing = 0, leaflet = root.L) {
    const angle = normalizedBearing(bearing) ?? 0;
    return leaflet.divIcon({ className: 'map-vehicle-marker',
      html: `<img class="map-vehicle-image" src="/assets/map/vehicle-${inProgress ? 'trip' : 'pickup'}.png" alt="${inProgress ? 'المركبة أثناء الرحلة' : 'موقع السائق'}" draggable="false" style="transform:rotate(${angle}deg)">`,
      iconSize: [46, 46], iconAnchor: [23, 23], popupAnchor: [0, -23] });
  }

  function stopIcon(destination = false, leaflet = root.L) {
    return leaflet.divIcon({ className: 'map-stop-marker',
      html: `<span class="map-stop-label">${destination ? 'الوجهة' : 'نقطة الانطلاق'}</span><img src="/assets/map/${destination ? 'destination' : 'pickup'}-pin.png" alt="" draggable="false">`,
      iconSize: [36, 41], iconAnchor: [18, 37], popupAnchor: [0, -48] });
  }

  // Rotate only the image: Leaflet owns the outer element's position transform.
  function setVehicleHeading(marker, value) {
    const angle = normalizedBearing(value);
    if (angle == null) return;
    marker._mapBearing = angle;
    const image = marker.getElement?.()?.querySelector?.('.map-vehicle-image');
    if (image) image.style.transform = `rotate(${angle}deg)`;
  }

  function movementBearing(from, to, supplied) {
    const explicit = normalizedBearing(supplied);
    if (explicit != null) return explicit;
    if (!from || !to) return null;
    const rad = Math.PI / 180, lat1 = from.lat * rad, lat2 = to.lat * rad;
    const dLon = (to.lon - from.lon) * rad;
    const north = (lat2 - lat1) * 6371000, east = dLon * Math.cos((lat1 + lat2) / 2) * 6371000;
    if (Math.hypot(north, east) < 5) return null;
    return (Math.atan2(Math.sin(dLon) * Math.cos(lat2),
      Math.cos(lat1) * Math.sin(lat2) - Math.sin(lat1) * Math.cos(lat2) * Math.cos(dLon)) / rad + 360) % 360;
  }

  function motionDuration(previousTime, nextTime, now = Date.now()) {
    const previous = Date.parse(previousTime), next = Date.parse(nextTime), gap = next - previous;
    // A recovered old fix must never replay the missing journey.
    if (!Number.isFinite(next) || now - next > 15000 || gap > 10000) return 0;
    return Math.min(850, Math.max(120, Number.isFinite(gap) && gap > 0 ? gap * .8 : 500));
  }

  root.AdminMaps = { attachBaseLayer, decodePolyline, directionsUrl, vehicleIcon, stopIcon,
    normalizedBearing, bearingDelta, setVehicleHeading, movementBearing, motionDuration };
  if (typeof module !== 'undefined' && module.exports) module.exports = root.AdminMaps;
})(typeof window !== 'undefined' ? window : globalThis);
