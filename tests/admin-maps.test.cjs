const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '../src/YemenDrive.Api/wwwroot');

function harness() {
  const nodes = new Map(), layers = [], maps = [], timers = new Map();
  let nextTimer = 0;
  class Node {
    constructor() {
      this.textContent = ''; this.innerHTML = ''; this.events = new Map(); this.dataset = {};
      const classes = new Set();
      this.classList = { add: c => classes.add(c), remove: c => classes.delete(c),
        contains: c => classes.has(c), toggle: (c, force) => force ? classes.add(c) : classes.delete(c) };
    }
    addEventListener(name, callback) { this.events.set(name, callback); }
    removeEventListener(name, callback) { if (this.events.get(name) === callback) this.events.delete(name); }
    querySelectorAll() { return []; }
    removeAttribute(name) { delete this[name]; }
    setAttribute(name, value) { this[name] = value; }
  }
  const node = selector => { if (!nodes.has(selector)) nodes.set(selector, new Node()); return nodes.get(selector); };
  class Layer {
    constructor(point, options) { this.point = point; this.options = options; this.events = new Map(); }
    on(name, callback) { this.events.set(name, callback); return this; }
    off() { this.events.clear(); return this; }
    fire(name) { this.events.get(name)?.(); }
    addTo(map) { map.layers.add(this); this.map = map; return this; }
    setLatLng(point) { this.point = point; return this; }
    getLatLng() { return this.point; }
    setLatLngs(points) { this.point = points; return this; }
    getLatLngs() { return this.point; }
    bindPopup() { return this; }
    bindTooltip() { return this; }
    getElement() { return null; }
    getBounds() { return this.point; }
  }
  const L = {
    tileLayer(url, options) { const layer = new Layer(null, options); layer.url = url; layers.push(layer); return layer; },
    map(id) {
      const map = { id, layers: new Set(), events: new Map(),
        on(name, callback) { this.events.set(name, callback); return this; },
        setView(point) { this.center = point; return this; },
        fitBounds(points) { this.bounds = points; return this; },
        removeLayer(layer) { this.layers.delete(layer); }, invalidateSize() {},
        remove() { this.events.get('unload')?.(); } };
      maps.push(map); return map;
    },
    marker: (point, options) => new Layer(point, options), circleMarker: (point, options) => new Layer(point, options),
    polyline: (points, options) => new Layer(points, options), divIcon: options => options
  };
  const context = { console, URLSearchParams, L, document: { querySelector: node, querySelectorAll: () => [], addEventListener() {} },
    setTimeout: callback => { timers.set(++nextTimer, callback); return nextTimer; }, clearTimeout: id => timers.delete(id),
    setInterval: () => 0, clearInterval() {}, cancelAnimationFrame() {}, addEventListener() {} };
  context.window = context;
  vm.createContext(context);
  for (const filename of ['admin-state-sync.js', 'admin-maps.js', 'admin-route-tracker.js', 'admin.js']) {
    vm.runInContext(fs.readFileSync(path.join(root, filename), 'utf8').replace(/^start\(\);\r?$/m, ''), context);
  }
  return { context, node, layers, maps, timers, read: code => vm.runInContext(code, context) };
}

test('blocked tile provider falls back, reports total failure, and can retry', () => {
  const h = harness(), map = h.context.L.map('test');
  const status = h.node('status'), retry = h.node('retry');
  const control = h.context.AdminMaps.attachBaseLayer(map, status, retry);
  assert.match(h.layers[0].url, /arcgisonline/);
  for (let i = 0; i < 3; i++) h.layers[0].fire('tileerror');
  assert.equal(control.providerIndex, 1);
  assert.match(h.layers[1].url, /tile.openstreetmap.fr/);
  h.layers[1].fire('tileerror'); h.layers[1].fire('load');
  assert.match(status.textContent, /تعذر تحميل/);
  assert.equal(retry.classList.contains('hidden'), false);
  retry.events.get('click')();
  assert.equal(control.providerIndex, 0);
  h.layers[2].fire('tileload'); h.layers[2].fire('load');
  assert.equal(status.textContent, '');
  assert.equal(retry.classList.contains('hidden'), true);
  assert.equal(map.layers.size, 1);
});

test('a hanging tile request times out and disposal prevents further requests', () => {
  const h = harness(), map = h.context.L.map('test');
  h.context.AdminMaps.attachBaseLayer(map, h.node('status'), h.node('retry'));
  [...h.timers.values()][0]();
  assert.equal(h.layers.length, 2);
  const pending = [...h.timers.values()].at(-1);
  map.remove();
  pending();
  assert.equal(h.layers.length, 2);
  assert.equal(h.timers.size, 0);
});

test('unavailable coordinates are not coerced into a real point at zero', () => {
  const h = harness();
  for (const pair of [[null, null], [null, 44], ['', 44], [15, undefined], [true, 44], [91, 44]]) {
    assert.equal(h.context.validMapPoint(...pair), null);
  }
  assert.equal(h.context.validMapPoint(0, 0).lat, 0);
  assert.equal(h.context.validMapPoint('15.4', '44.2').lon, 44.2);
});

test('REST recovery moves existing driver, passenger and destination markers', () => {
  const h = harness();
  const ride = { id: 1, driverLatitude: 15.4, driverLongitude: 44.2, passengerLatitude: 15.41, passengerLongitude: 44.21,
    pickupLatitude: 15.41, pickupLongitude: 44.21, destinationLatitude: 15.5, destinationLongitude: 44.3 };
  h.context.renderLiveRide(ride);
  const entry = h.read('liveRideMarkers.get(1)');
  const driver = entry.driver;
  h.context.renderLiveRide({ ...ride, driverLatitude: 15.42, passengerLatitude: 15.43, destinationLongitude: 44.4 });
  assert.equal(entry.driver, driver);
  assert.equal(driver.point[0], 15.42);
  assert.equal(entry.passenger.point[0], 15.43);
  assert.equal(entry.destination.point[1], 44.4);
  h.context.renderLiveRide({ ...ride, driverLatitude: null, driverLongitude: null });
  assert.equal(entry.driver, null);
});

test('route geometry comes only from the server polyline, rejecting corrupt data', () => {
  const h = harness();
  const encoded = '_p~iF~ps|U_ulLnnqC_mqNvxq`@';
  const points = h.context.AdminMaps.decodePolyline(encoded);
  assert.equal(points.length, 3);
  assert.equal(points[0][0], 38.5);
  assert.equal(points[2][1], -126.453);
  for (const invalid of [null, '', '_', 'zzzzzzzzzzzzzzzzz', 'A\u0000']) {
    assert.equal(h.context.AdminMaps.decodePolyline(invalid).length, 0);
  }
});

test('admin automatically selects a live ride and renders both real road legs without a click', async () => {
  const h = harness();
  const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures/admin-real-road-fixture.json'), 'utf8'));
  const ride = { id: 101, status: 3, driverLatitude: fixture.driver.latitude, driverLongitude: fixture.driver.longitude,
    pickupLatitude: fixture.pickup.latitude, pickupLongitude: fixture.pickup.longitude,
    destinationLatitude: fixture.destination.latitude, destinationLongitude: fixture.destination.longitude };
  const requests = [];
  h.context.execute = async (model, operation, data) => {
    requests.push({ model, operation, data });
    const leg = data.destinationLatitude === ride.pickupLatitude ? 'approach' : data.originLatitude === ride.pickupLatitude ? 'trip' : 'remaining';
    return { success: true, data: fixture.routes.find(route => route.name === leg).data };
  };
  h.read("state.view = 'rides'");
  h.read('state').liveRides.set(ride.id, ride);
  h.context.renderAllLiveRides();
  for (let index = 0; index < 16; index++) await Promise.resolve();
  assert.equal(h.read('selectedLiveRideId'), 101);
  assert.equal(requests.length, 2);
  assert.equal(requests[0].model, 'MapRouteQuoteModel');
  assert.equal(requests[0].operation, 'report');
  const routes = h.read('selectedLiveRideRoutes');
  assert.deepEqual([...routes.keys()].sort(), ['approach', 'trip']);
  assert.equal(routes.get('approach').options.color, '#08766a');
  assert.equal(routes.get('trip').options.color, '#2563eb');
  assert.ok(routes.get('approach').point.length > 50);
  assert.ok(routes.get('trip').point.length > 50);
  assert.ok(h.maps[0].bounds.length > 100);
  assert.match(h.node('#live-rides-list').innerHTML, /aria-pressed="true"/);
  h.context.applyRideStatusEvent({ rideId: 101, rideStatus: 5 });
  for (let index = 0; index < 16; index++) await Promise.resolve();
  assert.deepEqual([...routes.keys()], ['remaining']);
  assert.equal(requests.length, 3);
  assert.ok(routes.get('remaining').point.length > 50);
});

function liveFramingHarness(overrides) {
  const h = harness();
  const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures/admin-real-road-fixture.json'), 'utf8'));
  const ride = { id: 101, status: 5,
    driverLatitude: fixture.driver.latitude, driverLongitude: fixture.driver.longitude,
    pickupLatitude: fixture.pickup.latitude, pickupLongitude: fixture.pickup.longitude,
    destinationLatitude: fixture.destination.latitude, destinationLongitude: fixture.destination.longitude, ...overrides };
  h.context.execute = async (model, operation, data) => {
    assert.equal(model, 'MapRouteQuoteModel');
    const leg = data.originLatitude === ride.pickupLatitude ? 'trip' : 'remaining';
    return { data: fixture.routes.find(route => route.name === leg).data };
  };
  h.read("state.view = 'rides'");
  h.context.ensureLiveRidesMap();
  const map = h.maps[0], fitBounds = map.fitBounds;
  let fitCount = 0;
  map.fitBounds = function(points) { fitCount++; return fitBounds.call(this, points); };
  return { ...h, fixture, ride, map, get fitCount() { return fitCount; },
    async render(changes = {}) {
      Object.assign(ride, changes);
      h.read('state').liveRides.set(ride.id, { ...ride });
      h.context.renderAllLiveRides();
      await settleRoutes();
    } };
}

test('planned cancellation framing excludes current driver GPS and stays stable when it changes', async () => {
  const h = liveFramingHarness({ status: 8, driverLatitude: 25, driverLongitude: 50 });
  await h.render();
  const route = h.read('selectedLiveRideRoutes.get("planned")');
  assert.ok(route.point.length > 50, 'framing includes the real planned road geometry');
  assert.equal(h.fitCount, 1);
  assert.equal(h.map.bounds.length, route.point.length + 2, 'only pickup and destination supplement the planned road');
  assert.equal(h.map.bounds.some(point => point[0] === 25 && point[1] === 50), false);
  assert.equal(h.node('#live-trip-line-legend').classList.contains('legend-planned'), true);
  const bounds = JSON.stringify(h.map.bounds);
  await h.render({ driverLatitude: 26, driverLongitude: 51 });
  await h.render({ driverLatitude: null, driverLongitude: null });
  assert.equal(h.fitCount, 1, 'GPS availability cannot change planned framing in cancellation review');
  assert.equal(JSON.stringify(h.map.bounds), bounds);
  h.read('liveRouteTracker').dispose();
});

test('ongoing GPS loss and recovery refit once per route mode without following every GPS sample', async () => {
  const h = liveFramingHarness({ driverLatitude: null, driverLongitude: null });
  await h.render();
  assert.equal(h.fitCount, 1);
  assert.deepEqual([...h.read('selectedLiveRideRoutes').keys()], ['planned']);
  await h.render();
  assert.equal(h.fitCount, 1);

  const liveGps = { driverLatitude: h.fixture.driver.latitude, driverLongitude: h.fixture.driver.longitude };
  await h.render(liveGps);
  assert.equal(h.fitCount, 2, 'recovering live GPS reframes the remaining road');
  assert.deepEqual([...h.read('selectedLiveRideRoutes').keys()], ['remaining']);
  assert.equal(h.map.bounds.some(point => point[0] === liveGps.driverLatitude && point[1] === liveGps.driverLongitude), true);
  assert.equal(h.node('#live-trip-line-legend').classList.contains('legend-planned'), false);
  await h.render({ driverLatitude: liveGps.driverLatitude + 0.00001 });
  await h.render({ driverLatitude: liveGps.driverLatitude + 0.00002 });
  assert.equal(h.fitCount, 2, 'normal live movement preserves the operator map view');

  await h.render({ driverLatitude: null, driverLongitude: null });
  assert.equal(h.fitCount, 3, 'losing GPS reframes the complete planned road');
  assert.deepEqual([...h.read('selectedLiveRideRoutes').keys()], ['planned']);
  assert.equal(h.map.bounds.length, h.read('selectedLiveRideRoutes.get("planned")').point.length + 2);
  assert.equal(h.node('#live-trip-line-legend').classList.contains('legend-planned'), true);
  await h.render();
  assert.equal(h.fitCount, 3);
  await h.render(liveGps);
  assert.equal(h.fitCount, 4, 'a subsequent recovery also reframes exactly once');
  h.read('liveRouteTracker').dispose();
});

test('vehicle and stop icons use the supplied assets without fabricating a straight route', () => {
  const h = harness();
  assert.match(h.context.driverMarkerIcon(5).html, /vehicle-pickup\.png/);
  assert.match(h.context.driverMarkerIcon(5).html, /موقع السائق/);
  assert.match(h.context.driverMarkerIcon(5, true, 90).html, /vehicle-trip\.png/);
  assert.match(h.context.driverMarkerIcon(5, true, 90).html, /rotate\(90deg\)/);
  assert.match(h.context.AdminMaps.stopIcon().html, /نقطة الانطلاق/);
  assert.match(h.context.AdminMaps.stopIcon(true).html, /الوجهة/);
  h.context.ensureLiveRidesMap();
  h.read('selectedLiveRideId = 5');
  h.context.applySelectedLiveRoutes({ rideId: 5, status: 'error', legs: [], message: 'تعذر حساب الطريق' });
  assert.equal(h.read('selectedLiveRideRoutes').size, 0);
  assert.equal(h.node('#live-route-retry').classList.contains('hidden'), false);
});

test('starting the trip replaces the phase artwork and removes the overlapping passenger marker', () => {
  const h = harness(), ride = { id: 17, status: 3, driverLatitude: 15.4, driverLongitude: 44.2,
    passengerLatitude: 15.4, passengerLongitude: 44.2, driverBearing: 90 };
  h.context.renderLiveRide(ride);
  const entry = h.read('getLiveRideMarkerEntry(17)'), marker = entry.driver, passenger = entry.passenger;
  assert.ok(passenger);
  h.context.renderLiveRide({ ...ride, status: 5 });
  assert.equal(entry.driver, marker);
  assert.match(marker.options.icon.html, /vehicle-trip\.png/);
  assert.equal(entry.passenger, null);
  assert.equal(h.maps[0].layers.has(passenger), false);
  h.context.renderLiveRide({ ...ride, status: 3 });
  assert.match(marker.options.icon.html, /vehicle-pickup\.png/);
  assert.ok(entry.passenger);
});

test('live movement takes the shortest bearing turn and REST recovery cancels queued movement', () => {
  const h = harness(), frames = new Map(); let nextFrame = 0;
  h.context.performance = { now: () => 1000 };
  h.context.requestAnimationFrame = callback => { frames.set(++nextFrame, callback); return nextFrame; };
  h.context.cancelAnimationFrame = id => frames.delete(id);
  const now = Date.now(), ride = { id: 18, status: 3, driverLatitude: 15.4, driverLongitude: 44.2, driverBearing: 359,
    driverLocationObservedAtUtc: new Date(now - 1000).toISOString() };
  h.context.renderLiveRide(ride);
  h.read(`state.liveRides.set(18, ${JSON.stringify(ride)})`);
  h.context.animateLiveDriverLocation({ rideId: 18, latitude: 15.4001, longitude: 44.2, bearing: 1,
    observedAtUtc: new Date(now).toISOString() });
  const marker = h.read('getLiveRideMarkerEntry(18).driver');
  const [frame, callback] = [...frames][0]; frames.delete(frame); callback(1400);
  assert.ok(marker._mapBearing < .001 || marker._mapBearing > 359.999, `expected the short turn through north, got ${marker._mapBearing}`);
  assert.ok(marker.point[0] > 15.4 && marker.point[0] < 15.4001);
  h.context.renderLiveRide({ ...ride, driverLatitude: 15.4002, driverBearing: 5 });
  assert.equal(frames.size, 0);
  assert.equal(marker.point[0], 15.4002);
  assert.equal(marker._mapBearing, 5);
});

test('old or interrupted location samples are shown directly and do not replay missing travel', () => {
  const h = harness(), now = Date.now(), maps = h.context.AdminMaps;
  assert.equal(maps.motionDuration(new Date(now - 20000).toISOString(), new Date(now).toISOString(), now), 0);
  assert.equal(maps.motionDuration(new Date(now - 30000).toISOString(), new Date(now - 20000).toISOString(), now), 0);
  assert.ok(maps.motionDuration(new Date(now - 1000).toISOString(), new Date(now).toISOString(), now) <= 850);
  assert.equal(maps.movementBearing({ lat: 15, lon: 44 }, { lat: 15.000001, lon: 44 }, null), null);
  assert.equal(maps.movementBearing({ lat: 15, lon: 44 }, { lat: 15.001, lon: 44 }, null), 0);
});

test('safety map loads on demand, updates markers in place, and targets the current ride stage', () => {
  const h = harness();
  const item = { id: 2, rideId: 5, rideStatus: 3, driverLatitude: 15.4, driverLongitude: 44.2,
    pickupLatitude: 15.41, pickupLongitude: 44.21, destinationLatitude: 15.5, destinationLongitude: 44.3 };
  h.context.updateSafetyMap(item);
  assert.equal(h.maps.length, 0);
  assert.equal(new URL(h.node('#safety-map-route-link').href).searchParams.get('destination'), '15.41,44.21');
  assert.equal(h.context.renderSafetyMap(), true);
  h.read('safetyMapLoaded = true');
  h.context.updateSafetyMap({ ...item, rideStatus: 5, driverLatitude: 15.42 });
  assert.equal(h.maps.length, 1);
  assert.equal(h.read('safetyMapMarkers.get("driver")').point[0], 15.42);
  const route = new URL(h.node('#safety-map-route-link').href);
  assert.equal(route.hostname, 'www.google.com');
  assert.equal(route.searchParams.get('destination'), '15.5,44.3');
  assert.equal(route.searchParams.has('waypoints'), false);
  h.context.closeSafetyMap();
  assert.equal(h.read('safetyMap'), null);
  for (const callback of h.timers.values()) assert.doesNotThrow(callback);
});

function safetyHarness(status = 3, overrides = {}) {
  const h = harness();
  const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures/admin-real-road-fixture.json'), 'utf8'));
  const item = { id: 2, rideId: 101, rideStatus: status, driverLatitude: fixture.driver.latitude, driverLongitude: fixture.driver.longitude,
    latitude: fixture.pickup.latitude, longitude: fixture.pickup.longitude,
    pickupLatitude: fixture.pickup.latitude, pickupLongitude: fixture.pickup.longitude,
    destinationLatitude: fixture.destination.latitude, destinationLongitude: fixture.destination.longitude, ...overrides };
  const ride = { ...item, id: item.rideId, status, routeVersion: 0, serviceEndedAtUtc: null };
  const requests = [];
  const history = { rideId: item.rideId, plannedRoutePolyline: fixture.routes.find(route => route.name === 'trip').data.encodedPolyline, routeChanges: [] };
  h.context.execute = async (model, operation, data) => {
    requests.push({ model, operation, data });
    if (model === 'RideModel') return { data: { ride: { ...ride } } };
    if (model === 'RideMovementHistoryModel') return { data: history };
    const leg = data.destinationLatitude === item.pickupLatitude ? 'approach' : data.originLatitude === item.pickupLatitude ? 'trip' : 'remaining';
    return { data: fixture.routes.find(route => route.name === leg).data };
  };
  h.read("administratorPermissions = new Set(['operations.read'])");
  const load = () => {
    h.context.updateSafetyMap(item);
    assert.equal(requests.length, 0, 'no routing or history requests before loading the map');
    assert.equal(h.context.renderSafetyMap(), true);
    h.read('safetyMapLoaded = true');
    h.context.updateSafetyMap(item);
  };
  return { ...h, fixture, item, ride, history, requests, load };
}

async function settleRoutes() { for (let index = 0; index < 40; index++) await Promise.resolve(); }

test('safety map renders active road legs and switches to the remaining route', async () => {
  const h = safetyHarness();
  h.load(); await settleRoutes();
  assert.deepEqual([...h.read('safetyMapRoutes').keys()], ['approach', 'trip']);
  assert.ok(h.read('safetyMapRoutes.get("approach")').point.length > 50);
  assert.equal(h.requests.filter(request => request.model === 'MapRouteQuoteModel').length, 2);
  h.ride.status = 5;
  h.context.updateSafetyMap({ ...h.item, rideStatus: 5 });
  await settleRoutes();
  assert.deepEqual([...h.read('safetyMapRoutes').keys()], ['remaining']);
  const route = h.read('safetyMapRoutes.get("remaining")');
  assert.equal(route.options.color, '#2563eb');
  assert.ok(route.point.length > 50);
  assert.match(h.node('#safety-map-route-summary').textContent, /المتبقي/);
});

test('live and safety cancellation review maps show only a dashed planned road', async () => {
  const h = safetyHarness(8);
  h.read("state.view = 'rides'");
  h.read('state').liveRides.set(h.ride.id, h.ride);
  h.context.renderAllLiveRides();
  await settleRoutes();
  const liveRoutes = h.read('selectedLiveRideRoutes');
  assert.deepEqual([...liveRoutes.keys()], ['planned']);
  assert.equal(liveRoutes.get('planned').options.dashArray, '9 7');
  assert.ok(liveRoutes.get('planned').point.length > 50);
  const liveRequest = h.requests.find(request => request.model === 'MapRouteQuoteModel');
  assert.equal(liveRequest.data.originLatitude, h.item.pickupLatitude);
  assert.equal(liveRequest.data.destinationLatitude, h.item.destinationLatitude);
  h.requests.length = 0;
  h.load(); await settleRoutes();
  const safetyRoutes = h.read('safetyMapRoutes');
  assert.deepEqual([...safetyRoutes.keys()], ['planned']);
  assert.equal(safetyRoutes.get('planned').options.dashArray, '9 7');
  assert.equal(h.read('safetyMapMarkers.has("driver")'), false);
  assert.equal(h.requests.filter(request => request.model === 'MapRouteQuoteModel').length, 1);
  assert.match(h.node('#safety-map-route-summary').textContent, /لا يمثل حركة/);
});

test('ended safety rides render saved plans without using current driver GPS or calling road service', async () => {
  for (const status of [6, 7, 5]) {
    const h = safetyHarness(status, { driverLatitude: 25, driverLongitude: 50 });
    if (status === 5) h.ride.serviceEndedAtUtc = '2026-11-05T09:00:00Z';
    h.history.routeChanges.push({ id: 4, newRoutePolyline: h.history.plannedRoutePolyline });
    h.load(); await settleRoutes();
    assert.deepEqual([...h.read('safetyMapRoutes').keys()], ['saved-original', 'saved-change-4']);
    for (const layer of h.read('safetyMapRoutes').values()) {
      assert.equal(layer.options.dashArray, '9 7');
      assert.equal(layer.options.color, '#64748b');
    }
    assert.equal(h.requests.some(request => request.model === 'MapRouteQuoteModel'), false);
    assert.equal(h.read('safetyMapMarkers.has("driver")'), false);
    assert.equal(h.maps[0].bounds.some(point => point[0] === 25), false);
    assert.equal(h.node('#safety-map-route-link').classList.contains('hidden'), true);
    assert.match(h.node('#safety-map-route-summary').textContent, /مخطط محفوظ.*لا يثبت الحركة الفعلية/);
  }
});

test('missing historical geometry and missing permission remain explicit without invented lines', async () => {
  const h = safetyHarness(6, { driverLatitude: 25, driverLongitude: 50 });
  h.history.plannedRoutePolyline = null;
  h.load(); await settleRoutes();
  assert.equal(h.read('safetyMapRoutes.size'), 0);
  assert.equal(h.maps[0].bounds.some(point => point[0] === 25), false);
  assert.match(h.node('#safety-map-route-summary').textContent, /لا يوجد مسار مخطط محفوظ/);
  const denied = safetyHarness();
  denied.read('administratorPermissions = new Set()');
  denied.load(); await settleRoutes();
  assert.equal(denied.requests.length, 0);
  assert.equal(denied.read('safetyMapRoutes.size'), 0);
  assert.match(denied.node('#safety-map-route-summary').textContent, /صلاحية قراءة العمليات/);
});

test('safety route failures offer a retry that loads real road geometry', async () => {
  const h = safetyHarness();
  const transport = h.context.execute;
  let fail = true;
  h.context.execute = async (...args) => {
    if (args[0] === 'MapRouteQuoteModel' && fail) throw new Error('انقطاع الخدمة');
    return transport(...args);
  };
  h.load(); await settleRoutes();
  assert.equal(h.read('safetyMapRoutes.size'), 0);
  assert.equal(h.node('#safety-map-route-retry').classList.contains('hidden'), false);
  fail = false;
  await h.context.refreshSafetyMapRoutes(true); await settleRoutes();
  assert.deepEqual([...h.read('safetyMapRoutes').keys()], ['approach', 'trip']);
  assert.equal(h.node('#safety-map-route-retry').classList.contains('hidden'), true);
});

test('safety map ignores late canonical and route results after close or selection changes', async () => {
  const h = safetyHarness();
  const transport = h.context.execute;
  let resolveRide;
  h.context.execute = (...args) => args[0] === 'RideModel' ? new Promise(resolve => { resolveRide = resolve; }) : transport(...args);
  h.load();
  h.context.closeSafetyMap();
  resolveRide({ data: { ride: h.ride } }); await settleRoutes();
  assert.equal(h.read('safetyMap'), null);
  assert.equal(h.read('safetyMapRoutes.size'), 0);
  assert.equal(h.requests.length, 0);
  assert.equal(h.node('#safety-map-route-summary').textContent, '');

  const active = safetyHarness();
  const activeTransport = active.context.execute, pending = [];
  active.context.execute = (...args) => args[0] === 'MapRouteQuoteModel' ? new Promise(resolve => pending.push(resolve)) : activeTransport(...args);
  active.load(); await settleRoutes();
  assert.equal(pending.length, 2);
  active.context.updateSafetyMap({ ...active.item, id: 99, rideId: 202, rideStatus: 6 });
  for (const resolve of pending) resolve({ data: active.fixture.routes[0].data });
  await settleRoutes();
  assert.equal(active.read('safetyMapRoutes.size'), 0);
  assert.equal(active.read('safetyMapRide'), null);
});

test('safety canonical read timeout offers retry and cannot apply its late result', async () => {
  const h = safetyHarness();
  let resolveRide;
  h.context.execute = () => new Promise(resolve => { resolveRide = resolve; });
  h.load();
  h.read('safetyMapRouteRequest').timer;
  const timeout = h.timers.get(h.read('safetyMapRouteRequest').timer);
  timeout(); await settleRoutes();
  assert.equal(h.node('#safety-map-route-retry').classList.contains('hidden'), false);
  assert.match(h.node('#safety-map-route-summary').textContent, /مهلة/);
  resolveRide({ data: { ride: h.ride } }); await settleRoutes();
  assert.equal(h.read('safetyMapRoutes.size'), 0);
  assert.equal(h.read('safetyMapRide'), null);
});
