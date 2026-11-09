(function (root) {
  'use strict';

  const BEFORE_PICKUP = new Set([3, 4, 9]);
  const ROUTE_TTL_MS = 10 * 60 * 1000;
  const RECALCULATE_AFTER_MS = 30 * 1000;
  const REQUEST_TIMEOUT_MS = 20 * 1000;
  const OFF_ROUTE_METERS = 80;
  const REFRESH_PROGRESS_METERS = 200;
  const CACHE_LIMIT = 40;

  function point(latitude, longitude) {
    if (latitude == null || longitude == null || typeof latitude === 'boolean' || typeof longitude === 'boolean' ||
        String(latitude).trim() === '' || String(longitude).trim() === '') return null;
    const lat = Number(latitude), lon = Number(longitude);
    return Number.isFinite(lat) && Number.isFinite(lon) && Math.abs(lat) <= 90 && Math.abs(lon) <= 180 ? { lat, lon } : null;
  }

  const coordinate = (ride, prefix) => point(ride?.[`${prefix}Latitude`], ride?.[`${prefix}Longitude`]);
  const pointKey = value => value ? `${value.lat.toFixed(5)},${value.lon.toFixed(5)}` : '-';
  const routeKey = (origin, destination) => `${pointKey(origin)}>${pointKey(destination)}`;
  const asPoint = value => ({ lat: value[0], lon: value[1] });

  function distance(a, b) {
    const rad = Math.PI / 180;
    const lat = (b.lat - a.lat) * rad, lon = (b.lon - a.lon) * rad;
    const h = Math.sin(lat / 2) ** 2 + Math.cos(a.lat * rad) * Math.cos(b.lat * rad) * Math.sin(lon / 2) ** 2;
    return 6371000 * 2 * Math.atan2(Math.sqrt(h), Math.sqrt(Math.max(0, 1 - h)));
  }

  // Keep the next road vertex after the projected position. Removing the nearest
  // vertex instead would cut diagonally across the next street corner.
  function projectOnRoute(points, location) {
    const metersLat = Math.PI * 6371000 / 180;
    const metersLon = metersLat * Math.cos(location.lat * Math.PI / 180);
    let best = null, progress = 0;
    for (let index = 0; index < points.length - 1; index++) {
      const a = points[index], b = points[index + 1];
      const ax = (a[1] - location.lon) * metersLon, ay = (a[0] - location.lat) * metersLat;
      const bx = (b[1] - location.lon) * metersLon, by = (b[0] - location.lat) * metersLat;
      const dx = bx - ax, dy = by - ay, lengthSquared = dx * dx + dy * dy;
      const fraction = lengthSquared ? Math.min(1, Math.max(0, -(ax * dx + ay * dy) / lengthSquared)) : 0;
      const offset = Math.hypot(ax + fraction * dx, ay + fraction * dy);
      const segmentLength = distance(asPoint(a), asPoint(b));
      if (!best || offset < best.distanceMeters - 0.01) {
        best = { index, fraction, distanceMeters: offset, progressMeters: progress + fraction * segmentLength,
          projected: [a[0] + fraction * (b[0] - a[0]), a[1] + fraction * (b[1] - a[1])] };
      }
      progress += segmentLength;
    }
    if (!best) return null;
    const rest = points.slice(best.index + 1).map(value => [...value]);
    if (rest.length && distance(asPoint(rest[0]), asPoint(best.projected)) < 0.01) rest.shift();
    return { ...best, points: [best.projected, ...rest] };
  }

  class AdminRouteTracker {
    constructor({ fetchRoute, onChange = () => {}, decodePolyline = root.AdminMaps?.decodePolyline,
      now = Date.now, setTimer = (callback, delay) => root.setTimeout(callback, delay),
      clearTimer = timer => root.clearTimeout(timer) } = {}) {
      if (typeof fetchRoute !== 'function' || typeof decodePolyline !== 'function')
        throw new TypeError('Route transport and polyline decoder are required.');
      this.fetchRoute = fetchRoute;
      this.onChange = onChange;
      this.decodePolyline = decodePolyline;
      this.now = now;
      this.setTimer = setTimer;
      this.clearTimer = clearTimer;
      this.cache = new Map();
      this.inFlight = new Map();
      this.requestTimers = new Set();
      this.records = new Map();
      this.generation = 0;
      this.signature = '';
      this.ride = null;
      this.timer = null;
      this.disposed = false;
    }

    updateRide(ride, { force = false } = {}) {
      if (this.disposed) return;
      if (!ride || !Number.isSafeInteger(Number(ride.id)) || Number(ride.id) <= 0) { this.clear(); return; }
      this.ride = { ...ride };
      const signature = [Number(ride.id), Number(ride.status), pointKey(coordinate(ride, 'pickup')),
        pointKey(coordinate(ride, 'destination')), ride.routeVersion ?? ''].join('|');
      if (signature !== this.signature) {
        this.generation++;
        this.signature = signature;
        this.records.clear();
      }
      this._reconcile(force);
    }

    retry() {
      if (this.ride && !this.disposed) this.updateRide(this.ride, { force: true });
    }

    clear() {
      if (this.disposed) return;
      this.generation++;
      this.signature = '';
      this.ride = null;
      this.records.clear();
      this._cancelTimer();
      this.onChange({ rideId: null, status: 'idle', message: '', legs: [], updatedAt: null });
    }

    dispose() {
      if (this.disposed) return;
      this.clear();
      this.disposed = true;
      this.cache.clear();
      this.inFlight.clear();
      for (const timer of this.requestTimers) this.clearTimer(timer);
      this.requestTimers.clear();
    }

    _cancelTimer() {
      if (this.timer != null) this.clearTimer(this.timer);
      this.timer = null;
    }

    _specs() {
      const ride = this.ride;
      if (!ride) return [];
      const driver = coordinate(ride, 'driver'), pickup = coordinate(ride, 'pickup'), destination = coordinate(ride, 'destination');
      if (BEFORE_PICKUP.has(Number(ride.status))) return [
        { id: 'approach', origin: driver, destination: pickup, dynamic: true },
        { id: 'trip', origin: pickup, destination, dynamic: false }
      ];
      // A review or missing live GPS can show the planned road between the
      // requested endpoints, but must never imply the driver's actual movement.
      if (Number(ride.status) === 8 || Number(ride.status) === 5 && !driver)
        return [{ id: 'planned', origin: pickup, destination, dynamic: false }];
      if (Number(ride.status) === 5) return [{ id: 'remaining', origin: driver, destination, dynamic: true }];
      return [];
    }

    _reconcile(force = false) {
      if (this.disposed || !this.ride) return;
      this._cancelTimer();
      const specs = this._specs();
      if (!specs.length) {
        this.records.clear();
        this._emit('unavailable', 'المسار غير متاح في حالة الرحلة الحالية.');
        return;
      }
      const activeIds = new Set(specs.map(spec => spec.id));
      for (const id of this.records.keys()) if (!activeIds.has(id)) this.records.delete(id);
      let nextAttempt = Infinity;
      for (const spec of specs) {
        let record = this.records.get(spec.id);
        if (!record) {
          record = { id: spec.id, geometry: null, points: [], pending: null, failed: false,
            attemptAt: -Infinity, status: 'unavailable' };
          this.records.set(spec.id, record);
        }
        record.spec = spec;
        if (!spec.origin || !spec.destination) {
          record.points = [];
          record.status = 'unavailable';
          continue;
        }
        // A route between coincident points is not a road segment to draw.
        if (distance(spec.origin, spec.destination) < 8) {
          record.points = [];
          record.status = 'arrived';
          continue;
        }
        const projection = record.geometry && spec.dynamic ? projectOnRoute(record.geometry.points, spec.origin) : null;
        const onRoute = !spec.dynamic || projection && projection.distanceMeters <= OFF_ROUTE_METERS;
        record.points = record.geometry ?
          (spec.dynamic && onRoute ? projection.points : record.geometry.points).map(value => [...value]) : [];
        if (record.points.length < 2) record.points = [];
        if (record.pending) { record.status = 'loading'; continue; }
        if (record.failed && !force) {
          record.status = 'error';
          const retryAt = record.attemptAt + RECALCULATE_AFTER_MS;
          if (this.now() < retryAt) nextAttempt = Math.min(nextAttempt, retryAt);
          else this._startRequest(record, spec, true);
          continue;
        }
        const expired = record.geometry && this.now() - record.geometry.fetchedAt >= ROUTE_TTL_MS;
        const hasProgressed = projection && projection.progressMeters >= REFRESH_PROGRESS_METERS;
        const needsUpdate = force || !record.geometry || expired || spec.dynamic && (!onRoute || hasProgressed);
        if (!needsUpdate) { record.status = 'ready'; continue; }
        const earliest = record.attemptAt + RECALCULATE_AFTER_MS;
        if (!force && record.geometry && this.now() < earliest) {
          record.status = 'loading';
          nextAttempt = Math.min(nextAttempt, earliest);
          continue;
        }
        // A recalculation has already established that the prior geometry is
        // stale. Do not satisfy it from the same ten-minute route cache.
        this._startRequest(record, spec, force || Boolean(record.geometry));
      }
      if (Number.isFinite(nextAttempt)) {
        const generation = this.generation;
        this.timer = this.setTimer(() => {
          this.timer = null;
          if (!this.disposed && this.generation === generation) this._reconcile();
        }, Math.max(1, nextAttempt - this.now()));
      }
      this._emit();
    }

    _startRequest(record, spec, force) {
      const generation = this.generation;
      record.attemptAt = this.now();
      record.failed = false;
      record.status = 'loading';
      const request = this._request(spec.origin, spec.destination, force);
      record.pending = request;
      request.then(geometry => {
        if (this.disposed || this.generation !== generation || this.records.get(record.id) !== record || record.pending !== request) return;
        record.pending = null;
        record.geometry = geometry;
        record.failed = false;
        this._reconcile();
      }, () => {
        if (this.disposed || this.generation !== generation || this.records.get(record.id) !== record || record.pending !== request) return;
        record.pending = null;
        record.failed = true;
        record.status = 'error';
        this._reconcile();
      });
    }

    _request(origin, destination, force) {
      const key = routeKey(origin, destination);
      // Reusing the same pending request also covers switching away and back
      // before its network response arrives; only the current generation renders.
      if (this.inFlight.has(key)) return this.inFlight.get(key);
      const cached = this.cache.get(key);
      if (!force && cached && this.now() - cached.fetchedAt < ROUTE_TTL_MS) {
        this.cache.delete(key); this.cache.set(key, cached);
        return Promise.resolve(cached);
      }
      this.cache.delete(key);
      let requestTimer;
      const responseTask = new Promise((resolve, reject) => {
        requestTimer = this.setTimer(() => reject(new Error('The road request timed out.')), REQUEST_TIMEOUT_MS);
        this.requestTimers.add(requestTimer);
        Promise.resolve().then(() => this.fetchRoute({ ...origin }, { ...destination })).then(resolve, reject);
      });
      const request = responseTask.then(response => {
        const decoded = this.decodePolyline(response?.encodedPolyline);
        if (!Array.isArray(decoded) || decoded.length < 2 || decoded.length > 100000 ||
            decoded.some(value => !Array.isArray(value) || value.length < 2 || !point(value[0], value[1])))
          throw new Error('The route provider returned no valid road geometry.');
        const points = decoded.map(value => [Number(value[0]), Number(value[1])]);
        if (!points.some(value => distance(asPoint(points[0]), asPoint(value)) > 0.1))
          throw new Error('The route provider returned a degenerate road geometry.');
        const geometry = { points, origin: { ...origin }, destination: { ...destination }, fetchedAt: this.now(),
          distanceMeters: Number.isFinite(response.distanceMeters) ? response.distanceMeters : null,
          durationSeconds: Number.isFinite(response.durationSeconds) ? response.durationSeconds : null };
        if (!this.disposed) {
          this.cache.set(key, geometry);
          while (this.cache.size > CACHE_LIMIT) this.cache.delete(this.cache.keys().next().value);
        }
        return geometry;
      }).finally(() => {
        this.clearTimer(requestTimer);
        this.requestTimers.delete(requestTimer);
        if (this.inFlight.get(key) === request) this.inFlight.delete(key);
      });
      this.inFlight.set(key, request);
      return request;
    }

    _emit(statusOverride, messageOverride) {
      if (this.disposed || !this.ride) return;
      const records = [...this.records.values()];
      const legs = records.filter(record => record.points.length >= 2).map(record => ({
        id: record.id, points: record.points.map(value => [...value]),
        distanceMeters: record.geometry?.distanceMeters ?? null,
        durationSeconds: record.geometry?.durationSeconds ?? null
      }));
      const loading = records.some(record => record.status === 'loading');
      const failed = records.some(record => record.status === 'error');
      const unavailable = records.some(record => record.status === 'unavailable');
      const status = statusOverride || (loading ? 'loading' : failed || unavailable ?
        legs.length ? 'partial' : failed ? 'error' : 'unavailable' : 'ready');
      let message = messageOverride;
      if (message == null) {
        if (records.some(record => record.id === 'planned')) {
          const context = Number(this.ride.status) === 8 ? 'الإلغاء قيد المراجعة؛ ' : 'موقع السائق الحالي غير متاح؛ ';
          if (loading) message = context + (legs.length ? 'جارٍ تحديث الطريق المخطط بين الالتقاط والوجهة…' : 'جارٍ حساب الطريق المخطط بين الالتقاط والوجهة…');
          else if (failed) message = context + (legs.length ? 'تعذر تحديث الطريق المخطط. يظهر آخر طريق مخطط متاح؛ أعد المحاولة.' : 'تعذر حساب الطريق المخطط. أعد المحاولة.');
          else if (unavailable) message = context + 'لا تتوفر نقطتا الالتقاط والوجهة لحساب الطريق المخطط.';
          else if (!legs.length) message = context + 'نقطة الالتقاط والوجهة متقاربتان؛ لا يوجد مقطع طريق لعرضه.';
          else message = context + 'يظهر الطريق المخطط من الالتقاط إلى الوجهة، ولا يمثل حركة السائق الفعلية.';
        }
        else if (loading) message = legs.length ? 'جارٍ تحديث الطريق وفق موقع السائق…' : 'جارٍ حساب الطريق على الشوارع…';
        else if (failed) message = legs.length ? 'تعذر تحديث أحد المسارات. يظهر آخر طريق متاح؛ أعد المحاولة.' : 'تعذر حساب الطريق. أعد المحاولة.';
        else if (unavailable) message = legs.length ? 'يظهر المسار المتاح؛ ننتظر إحداثيات السائق أو نقاط الرحلة لبقية الطريق.' : 'لا تتوفر إحداثيات كافية لحساب الطريق.';
        else if (!legs.length) message = 'السائق عند نهاية المسار.';
        else message = BEFORE_PICKUP.has(Number(this.ride.status)) ? 'الطريق إلى الراكب ومسار الرحلة على الشوارع.' : 'الطريق المتبقي إلى الوجهة على الشوارع.';
      }
      this.onChange({ rideId: Number(this.ride.id), status, message, legs,
        updatedAt: records.reduce((latest, record) => Math.max(latest, record.geometry?.fetchedAt ?? 0), 0) || null });
    }
  }

  root.AdminRouteTracker = AdminRouteTracker;
  if (typeof module !== 'undefined' && module.exports) module.exports = { AdminRouteTracker };
})(typeof window !== 'undefined' ? window : globalThis);
