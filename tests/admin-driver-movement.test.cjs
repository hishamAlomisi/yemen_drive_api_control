const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '../src/YemenDrive.Api/wwwroot');

function harness(permissions = ['*']) {
  const nodes = new Map(), maps = [], requests = [], events = new Map();
  class Node {
    constructor() { this.textContent = ''; this.innerHTML = ''; this.value = ''; this.dataset = {}; this.events = new Map();
      const classes = new Set(); this.classList = { add: c => classes.add(c), remove: c => classes.delete(c), contains: c => classes.has(c), toggle: (c, force) => force ? classes.add(c) : classes.delete(c) }; }
    addEventListener(name, fn) { this.events.set(name, fn); }
    removeEventListener(name) { this.events.delete(name); }
    setAttribute(name, value) { this[name] = value; }
    removeAttribute(name) { delete this[name]; }
    querySelectorAll() { return []; }
    closest() { return this; }
    showModal() { this.open = true; }
    close() { this.open = false; this.events.get('close')?.(); }
  }
  const node = selector => { if (!nodes.has(selector)) nodes.set(selector, new Node()); return nodes.get(selector); };
  class Layer {
    constructor(point, options) { this.point = point; this.options = options; this.events = new Map(); this.dragging = { enable() {}, disable() {} }; }
    addTo(map) { this.map = map; map.layers.add(this); return this; }
    on(name, fn) { this.events.set(name, fn); return this; }
    off() { this.events.clear(); }
    setLatLng(point) { this.point = point; return this; }
    getLatLng() { return { lat: this.point[0], lng: this.point[1] }; }
    bindTooltip(text) { this.tooltip = text; return this; }
    bindPopup(text) { this.popup = text; return this; }
  }
  const L = { map(id) { const map = { id, layers: new Set(), events: new Map(), setView(point) { this.center = point; return this; },
    on(name, fn) { this.events.set(name, fn); return this; }, removeLayer(layer) { this.layers.delete(layer); }, fitBounds(bounds) { this.bounds = bounds; },
    invalidateSize() {}, remove() { this.removed = true; this.events.get('unload')?.(); } }; maps.push(map); return map; },
    marker: (point, options) => new Layer(point, options), circleMarker: (point, options) => new Layer(point, options),
    polyline: (point, options) => new Layer(point, options), tileLayer: (url, options) => new Layer(null, options), divIcon: x => x };
  const context = { console, Intl, Date, URL, URLSearchParams, L, document: { querySelector: node, querySelectorAll: () => [],
    addEventListener: (name, fn) => events.set(name, fn) }, setTimeout: () => 0, clearTimeout() {}, setInterval: () => 0, clearInterval() {},
    cancelAnimationFrame() {}, addEventListener() {} };
  context.window = context;
  vm.createContext(context);
  for (const file of ['admin-state-sync.js', 'admin-maps.js', 'admin-route-tracker.js', 'admin.js', 'admin-driver-movement.js'])
    vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8').replace(/^start\(\);\r?$/m, ''), context);
  const read = code => vm.runInContext(code, context);
  read(`administratorPermissions = new Set(${JSON.stringify(permissions)})`);
  context.execute = async (model, operation, data, options) => { requests.push({ model, operation, data, options }); return { data: {} }; };
  return { context, node, maps, requests, read, events };
}
const snapshot = (changes = {}) => ({ driverId: 10, location: { latitude: 15.4, longitude: 44.2, observedAtUtc: '2026-11-05T10:00:00Z' }, ride: { id: 9, status: 4 }, canMove: true, ...changes });
function deferred() { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; }

test('map clicks and dragging are drafts; only save sends the chosen coordinates', async () => {
  const h = harness();
  h.context.execute = async (model, operation, data, options) => {
    h.requests.push({ model, operation, data, options });
    return { data: snapshot(operation === 'save' ? { location: { latitude: data.latitude, longitude: data.longitude } } : {}) };
  };
  await h.context.openDriverLocation(10);
  h.maps[0].events.get('click')({ latlng: { lat: 15.45, lng: 44.25 } });
  assert.equal(h.requests.length, 1);
  assert.equal(h.node('#driver-location-save').disabled, false);
  assert.equal(h.node('#driver-location-move').disabled, true);
  await h.context.writeDriverLocation('save');
  assert.equal(h.requests[1].operation, 'save');
  assert.equal(h.requests[1].data.latitude, 15.45);
  assert.equal(h.requests[1].data.longitude, 44.25);
  assert.equal(h.requests[1].options.confirmed, true);
  assert.equal(h.node('#driver-location-save').disabled, true);
  const marker = h.read('driverLocationEditor.marker');
  marker.point = [15.46, 44.26]; marker.events.get('dragend')();
  assert.equal(h.requests.length, 2);
  assert.equal(h.read('driverLocationEditor.draft.lat'), 15.46);
});

test('one move click sends one server step, locks pending clicks and discloses road snapping', async () => {
  const h = harness(), pending = deferred();
  h.context.execute = async (model, operation, data, options) => {
    h.requests.push({ model, operation, data, options });
    return operation === 'move' ? pending.promise : { data: snapshot() };
  };
  await h.context.openDriverLocation(10);
  const first = h.context.writeDriverLocation('move');
  await h.context.writeDriverLocation('move');
  assert.equal(h.requests.length, 2);
  assert.deepEqual(Object.keys(h.requests[1].data), ['driverId']);
  assert.equal(h.node('#driver-location-move').disabled, true);
  pending.resolve({ data: snapshot({ snapDistanceMeters: 5.5, distanceMeters: .75 }) }); await first;
  assert.match(h.node('#driver-location-feedback').textContent, /5.5/);
  assert.match(h.node('#driver-location-feedback').textContent, /0.75/);
  assert.equal(h.node('#driver-location-move').disabled, false);
});

test('drivers without active routes and viewers without write permission cannot step', async () => {
  const h = harness(); h.context.execute = async () => ({ data: snapshot({ canMove: false, ride: null }) });
  await h.context.openDriverLocation(10); await h.context.writeDriverLocation('move');
  assert.equal(h.node('#driver-location-move').disabled, true);
  const readOnly = harness(['personal_data.read']);
  readOnly.context.execute = async (...args) => { readOnly.requests.push(args); return { data: snapshot() }; };
  await readOnly.context.openDriverLocation(10);
  readOnly.maps[0].events.get('click')({ latlng: { lat: 16, lng: 45 } });
  await readOnly.context.writeDriverLocation('save'); await readOnly.context.writeDriverLocation('move');
  assert.equal(readOnly.requests.length, 1);
  assert.equal(readOnly.node('#driver-location-save').disabled, true);
  assert.equal(readOnly.node('#driver-location-move').disabled, true);
});

test('missing driver GPS is not displayed as a fabricated saved location', async () => {
  const h = harness(); h.context.execute = async () => ({ data: snapshot({ location: null, canMove: false }) });
  await h.context.openDriverLocation(10);
  assert.equal(h.read('driverLocationEditor.marker'), null);
  assert.equal(h.node('#driver-location-latitude').value, '');
  assert.match(h.node('#driver-location-details').textContent, /لا يوجد موقع محفوظ/);
});

test('closing location dialog rejects late read results and removes map', async () => {
  const h = harness(), pending = deferred(); h.context.execute = async () => pending.promise;
  const opening = h.context.openDriverLocation(10);
  h.node('#driver-location-dialog').close();
  pending.resolve({ data: snapshot() }); await opening;
  assert.equal(h.read('driverLocationEditor.map'), null);
  assert.equal(h.read('driverLocationEditor.snapshot'), null);
  assert.equal(h.maps[0].removed, true);
});

test('report date range includes all of the last Yemen day and validates driver and order', () => {
  const h = harness(); h.node('#driver-movement-driver').value = '10';
  h.node('#driver-movement-from').value = '2026-11-01'; h.node('#driver-movement-to').value = '2026-11-05';
  const filters = h.context.driverMovementFilters();
  assert.equal(filters.fromUtc, '2026-10-31T21:00:00.000Z');
  assert.equal(filters.toUtc, '2026-11-05T21:00:00.000Z');
  h.node('#driver-movement-from').value = '2026-11-06'; assert.throws(() => h.context.driverMovementFilters());
  h.node('#driver-movement-driver').value = ''; assert.throws(() => h.context.driverMovementFilters());
});

test('missing durations and ratings remain unknown; report escapes user text and links the exact ride', () => {
  const h = harness();
  const html = h.context.driverMovementReportRow({ rideId: 42, status: 6, customerName: '<script>alert(1)</script>', complaintCount: 2, openComplaintCount: 1 });
  assert.match(html, /غير مسجلة/); assert.match(html, /لم تُقيّم/); assert.match(html, /&lt;script&gt;/);
  assert.doesNotMatch(html, /<script>/); assert.match(html, /data-movement-map="42"/); assert.match(html, /data-movement-complaints="42"/);
});

test('delay assessment distinguishes exceeded estimate, within estimate and an unknown baseline', () => {
  const h = harness();
  const exceeded = h.context.driverMovementReportRow({ rideId: 42, status: 6, tripDelayAssessed: true, tripDelaySeconds: 90, plannedTripDurationSeconds: 600 });
  assert.match(exceeded, /تجاوز المدة المقدرة: 1 د 30 ث/);
  const within = h.context.driverMovementReportRow({ rideId: 42, status: 6, tripDelayAssessed: true, tripDelaySeconds: 0 });
  assert.match(within, /ضمن المدة المقدرة/);
  const missing = h.context.driverMovementReportRow({ rideId: 42, status: 5, tripDelayAssessed: false, activeDelayCount: 1, delayCount: 1 });
  assert.match(missing, /غير قابل للتقييم/); assert.match(missing, /1 انتظار جارٍ/);
});

test('a slow earlier report cannot replace the latest filters', async () => {
  const h = harness(), pending = deferred();
  h.read('driverMovementReport.filters = {driverId: 10}');
  let count = 0;
  h.context.execute = async () => ++count === 1 ? pending.promise : { data: { page: 1, pageCount: 1, totalCount: 1, rows: [{ id: 2, customerName: 'الجديد' }] } };
  const first = h.context.showDriverMovementReport();
  h.read('driverMovementReport.filters = {driverId: 11}'); await h.context.showDriverMovementReport();
  pending.resolve({ data: { page: 1, pageCount: 1, totalCount: 1, rows: [{ id: 1, customerName: 'القديم' }] } }); await first;
  assert.match(h.node('#driver-movement-body').innerHTML, /الجديد/);
  assert.doesNotMatch(h.node('#driver-movement-body').innerHTML, /القديم/);
});

test('archived route gaps and invalid points are never bridged by a line', () => {
  const h = harness();
  const samples = [
    { latitude: 15, longitude: 44, observedAtUtc: '2026-11-05T10:00:00Z' },
    { latitude: 15.01, longitude: 44.01, observedAtUtc: '2026-11-05T10:00:10Z' },
    { latitude: 15.1, longitude: 44.1, observedAtUtc: '2026-11-05T10:04:00Z' },
    { latitude: null, longitude: 44.2, observedAtUtc: '2026-11-05T10:04:10Z' },
    { latitude: 15.2, longitude: 44.2, observedAtUtc: '2026-11-05T10:04:20Z' }
  ];
  const segments = h.context.movementHistorySegments(samples);
  assert.deepEqual([...segments].map(x => x.length), [2, 1, 1]);
});

test('historical map uses real archived stages, different colors and explicit missing origins', async () => {
  const h = harness();
  h.context.execute = async () => ({ data: { rideId: 42, status: 6, notes: ['السجل القديم جزئي.'],
    driverAtRequest: null, passengerAtRequest: null, pickup: { latitude: 15, longitude: 44 }, destination: { latitude: 15.2, longitude: 44.2 },
    approachSamples: [{ latitude: 14.9, longitude: 43.9 }, { latitude: 15, longitude: 44 }],
    tripSamples: [{ latitude: 15, longitude: 44 }, { latitude: 15.2, longitude: 44.2 }],
    plannedRoutePolyline: '_p~iF~ps|U_ulLnnqC_mqNvxq`@' } });
  await h.context.openRideMovementMap(42);
  const lines = [...h.maps[0].layers].filter(x => x.options?.weight === 5);
  assert.equal(lines.length, 2); assert.deepEqual(lines.map(x => x.options.color), ['#2563eb', '#08766a']);
  assert.match(h.node('#ride-movement-notes').innerHTML, /السائق عند الطلب: غير مسجل/);
  assert.match(h.node('#ride-movement-notes').innerHTML, /الراكب عند الطلب: غير مسجل/);
  const planned = [...h.maps[0].layers].find(x => x.options?.dashArray === '8 7');
  assert.equal(planned.options.color, '#64748b'); assert.equal(planned.tooltip, 'المسار المخطط للرحلة');
  assert.match(h.node('#ride-movement-feedback').textContent, /لا يثبت أن السائق سلكه/);
});

test('late history response after closing cannot create map layers', async () => {
  const h = harness(), pending = deferred(); h.context.execute = async () => pending.promise;
  const opening = h.context.openRideMovementMap(42); h.node('#ride-movement-dialog').close();
  pending.resolve({ data: { rideId: 42, notes: [] } }); await opening;
  assert.equal(h.read('rideMovementMap.map'), null); assert.equal(h.maps[0].removed, true);
});

test('server segment boundaries survive a manual relocation even with close timestamps', async () => {
  const h = harness();
  const first = { latitude: 15, longitude: 44, observedAtUtc: '2026-11-05T10:00:00Z' };
  const next = { latitude: 15.1, longitude: 44.1, observedAtUtc: '2026-11-05T10:00:05Z', source: 'admin_manual' };
  h.context.execute = async () => ({ data: { rideId: 42, approachSamples: [first, next], approachSegments: [[first], [next]], tripSegments: [], notes: [] } });
  await h.context.openRideMovementMap(42);
  assert.equal([...h.maps[0].layers].filter(x => x.options?.weight === 5).length, 0);
  assert.equal([...h.maps[0].layers].filter(x => x.options?.radius === 4).length, 2);
});

test('driver search passes typed name without requiring broader personal-data access', async () => {
  const h = harness(['operations.read']); h.node('#driver-movement-search').value = '  اسم السائق  ';
  h.context.execute = async (model, operation, data) => { h.requests.push({ model, operation, data }); return { data: [{ id: 10, displayName: 'اسم السائق', phoneNumber: '777' }] }; };
  await h.context.loadDriverMovementReport();
  assert.equal(h.requests[0].model, 'DriverMovementReportModel'); assert.equal(h.requests[0].operation, 'search');
  assert.equal(h.requests[0].data.search, 'اسم السائق'); assert.match(h.node('#driver-movement-driver').innerHTML, /value="10"/);
});

test('complaints request both exact-ride sources and exclude unrelated returned records', async () => {
  const h = harness();
  h.context.execute = async (model, operation, data) => {
    h.requests.push({ model, operation, data });
    return { data: [{ id: 1, rideId: 42, status: model === 'SafetyAdminReportModel' ? 0 : 'Open', message: 'بلاغ الرحلة' },
      { id: 2, rideId: 99, message: 'رحلة أخرى' }] };
  };
  await h.context.openRideMovementComplaints(42);
  assert.equal(h.requests.length, 2);
  assert.ok(h.requests.every(x => x.data.rideId === 42));
  assert.match(h.node('#ride-complaints-support').innerHTML, /بلاغ الرحلة/);
  assert.match(h.node('#ride-complaints-safety').innerHTML, /بلاغ الرحلة/);
  assert.doesNotMatch(h.node('#ride-complaints-support').innerHTML, /رحلة أخرى/);
});

test('complaint permissions remain separate and missing scope is visible', async () => {
  const h = harness(['support.read']);
  h.context.execute = async (model) => { h.requests.push(model); return { data: [] }; };
  await h.context.openRideMovementComplaints(42);
  assert.deepEqual(h.requests, ['SupportTicketModel']);
  assert.match(h.node('#ride-complaints-safety').textContent, /لا تتوفر صلاحية/);
});

test('logout clears report data, closes map dialogs and invalidates complaints', () => {
  const h = harness(); h.node('#driver-movement-body').innerHTML = 'private';
  h.node('#ride-complaints-support').innerHTML = 'private'; h.node('#ride-movement-dialog').showModal();
  const generation = h.read('rideMovementComplaintGeneration');
  h.context.resetDriverMovement();
  assert.equal(h.node('#driver-movement-body').innerHTML, '');
  assert.equal(h.node('#ride-complaints-support').innerHTML, '');
  assert.equal(h.node('#ride-movement-dialog').open, false);
  assert.ok(h.read('rideMovementComplaintGeneration') > generation);
});
