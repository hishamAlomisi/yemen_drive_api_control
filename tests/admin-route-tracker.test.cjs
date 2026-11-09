const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const exported = require('../src/YemenDrive.Api/wwwroot/admin-route-tracker.js');
const AdminRouteTracker = exported.AdminRouteTracker || exported;

const settle = async () => { await new Promise(resolve => setImmediate(resolve)); await new Promise(resolve => setImmediate(resolve)); };
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((done, fail) => { resolve = done; reject = fail; });
  return { promise, resolve, reject };
};
const sampleRide = (overrides = {}) => ({ id: 101, status: 3,
  driverLatitude: 15.4, driverLongitude: 44.2,
  pickupLatitude: 15.405, pickupLongitude: 44.205,
  destinationLatitude: 15.41, destinationLongitude: 44.212,
  ...overrides });
const point = (ride, prefix) => ({ lat: ride[`${prefix}Latitude`], lon: ride[`${prefix}Longitude`] });
const response = points => ({ encodedPolyline: JSON.stringify(points), distanceMeters: 1200, durationSeconds: 180 });
const routeResponse = (origin, destination) => response([[origin.lat, origin.lon],
  [origin.lat, destination.lon], [destination.lat, destination.lon]]);

function harness(transport = (origin, destination) => Promise.resolve(routeResponse(origin, destination))) {
  let clock = 1700000000000, timerId = 0;
  const requests = [], states = [], timers = new Map();
  const tracker = new AdminRouteTracker({
    fetchRoute(origin, destination) {
      requests.push({ origin: { ...origin }, destination: { ...destination } });
      return transport(origin, destination, requests.length - 1);
    },
    onChange: state => states.push(JSON.parse(JSON.stringify(state))),
    decodePolyline: encoded => {
      try { const points = JSON.parse(encoded); return Array.isArray(points) ? points : []; }
      catch (_) { return []; }
    },
    now: () => clock,
    setTimer(callback, delay) { const id = ++timerId; timers.set(id, { callback, due: clock + delay }); return id; },
    clearTimer: id => timers.delete(id)
  });
  return { tracker, requests, states, timers,
    get state() { return states.at(-1); },
    async advance(milliseconds) {
      const target = clock + milliseconds;
      let iterations = 0;
      while (true) {
        const pending = [...timers.entries()].filter(([, timer]) => timer.due <= target)
          .sort((a, b) => a[1].due - b[1].due)[0];
        if (!pending) break;
        assert.ok(++iterations < 100, 'timer scheduling must not create a busy retry loop');
        clock = pending[1].due;
        timers.delete(pending[0]);
        pending[1].callback();
        await settle();
      }
      clock = target;
      await settle();
    }
  };
}

test('initial assigned trip requests two real driving legs and exposes both without a click', async () => {
  const h = harness(), ride = sampleRide();
  h.tracker.updateRide(ride);
  await settle();
  assert.equal(h.requests.length, 2);
  assert.deepEqual(h.requests[0], { origin: point(ride, 'driver'), destination: point(ride, 'pickup') });
  assert.deepEqual(h.requests[1], { origin: point(ride, 'pickup'), destination: point(ride, 'destination') });
  assert.equal(h.state.rideId, ride.id);
  assert.equal(h.state.status, 'ready');
  assert.deepEqual(h.state.legs.map(leg => leg.id).sort(), ['approach', 'trip']);
  assert.ok(h.state.legs.every(leg => leg.points.length >= 3));
  assert.ok(h.states.some(state => state.status === 'loading'));
  h.tracker.dispose();
});

test('ongoing trip requests the current driver to destination instead of routing back to pickup', async () => {
  const h = harness(), ride = sampleRide({ status: 5 });
  h.tracker.updateRide(ride);
  await settle();
  assert.equal(h.requests.length, 1);
  assert.deepEqual(h.requests[0], { origin: point(ride, 'driver'), destination: point(ride, 'destination') });
  assert.deepEqual(h.state.legs.map(leg => leg.id), ['remaining']);
  assert.equal(h.state.status, 'ready');
  h.tracker.dispose();
});

test('repeated identical snapshots reuse pending requests and still publish their eventual routes', async () => {
  const pending = [deferred(), deferred()];
  const h = harness((origin, destination, index) => pending[index].promise);
  const ride = sampleRide();
  for (let index = 0; index < 20; index++) h.tracker.updateRide({ ...ride });
  await settle();
  assert.equal(h.requests.length, 2);
  pending[0].resolve(routeResponse(point(ride, 'driver'), point(ride, 'pickup')));
  pending[1].resolve(routeResponse(point(ride, 'pickup'), point(ride, 'destination')));
  await settle();
  assert.equal(h.requests.length, 2);
  assert.equal(h.state.status, 'ready');
  assert.equal(h.state.legs.length, 2);
  h.tracker.dispose();
});

test('late selected-ride response cannot overwrite a different selected ride', async () => {
  const pending = [];
  const h = harness(() => { const item = deferred(); pending.push(item); return item.promise; });
  const first = sampleRide({ status: 5 });
  const second = sampleRide({ id: 102, status: 5, driverLatitude: 15.42, destinationLatitude: 15.43 });
  h.tracker.updateRide(first);
  h.tracker.updateRide(second);
  await settle();
  assert.equal(h.requests.length, 2);
  pending[1].resolve(routeResponse(point(second, 'driver'), point(second, 'destination')));
  await settle();
  const accepted = JSON.stringify(h.state);
  pending[0].resolve(routeResponse(point(first, 'driver'), point(first, 'destination')));
  await settle();
  assert.equal(h.state.rideId, second.id);
  assert.equal(JSON.stringify(h.state), accepted);
  h.tracker.dispose();
});

test('start-of-trip transition discards old approach results and preserves destination navigation', async () => {
  const pending = [];
  const h = harness(() => { const item = deferred(); pending.push(item); return item.promise; });
  const assigned = sampleRide(), started = { ...assigned, status: 5 };
  h.tracker.updateRide(assigned);
  h.tracker.updateRide(started);
  await settle();
  assert.equal(h.requests.length, 3);
  pending[2].resolve(routeResponse(point(started, 'driver'), point(started, 'destination')));
  await settle();
  pending[0].resolve(routeResponse(point(assigned, 'driver'), point(assigned, 'pickup')));
  pending[1].resolve(routeResponse(point(assigned, 'pickup'), point(assigned, 'destination')));
  await settle();
  assert.deepEqual(h.state.legs.map(leg => leg.id), ['remaining']);
  assert.equal(h.state.status, 'ready');
  h.tracker.dispose();
});

test('destination change does not let an older route response redraw the previous destination', async () => {
  const pending = [];
  const h = harness(() => { const item = deferred(); pending.push(item); return item.promise; });
  const first = sampleRide({ status: 5 }), changed = { ...first, destinationLongitude: 44.23 };
  h.tracker.updateRide(first);
  h.tracker.updateRide(changed);
  await settle();
  assert.equal(h.requests.length, 2);
  pending[1].resolve(routeResponse(point(changed, 'driver'), point(changed, 'destination')));
  await settle();
  pending[0].resolve(routeResponse(point(first, 'driver'), point(first, 'destination')));
  await settle();
  const finalPoint = h.state.legs[0].points.at(-1);
  assert.deepEqual(finalPoint, [changed.destinationLatitude, changed.destinationLongitude]);
  assert.equal(h.state.status, 'ready');
  h.tracker.dispose();
});

test('GPS updates clip the existing road geometry without making a request for every location', async () => {
  const ride = sampleRide({ status: 5, driverLatitude: 15.4, driverLongitude: 44.2,
    destinationLatitude: 15.402, destinationLongitude: 44.202 });
  const road = [[15.4, 44.2], [15.4, 44.202], [15.402, 44.202]];
  const h = harness(() => Promise.resolve(response(road)));
  h.tracker.updateRide(ride);
  await settle();
  for (let index = 1; index <= 20; index++) {
    h.tracker.updateRide({ ...ride, driverLongitude: 44.2 + index * 0.000095 });
    await settle();
  }
  assert.equal(h.requests.length, 1);
  const remaining = h.state.legs.find(leg => leg.id === 'remaining').points;
  assert.ok(Math.abs(remaining[0][0] - 15.4) < 0.000001);
  assert.ok(Math.abs(remaining[0][1] - 44.2019) < 0.000001);
  assert.deepEqual(remaining[1], [15.4, 44.202], 'the next corner remains on the path instead of being skipped');
  assert.deepEqual(remaining.at(-1), [15.402, 44.202]);
  h.tracker.dispose();
});

test('off-route GPS is coalesced to the latest position and recomputed after the throttle window', async () => {
  const h = harness(), ride = sampleRide({ status: 5 });
  h.tracker.updateRide(ride);
  await settle();
  await h.advance(1000);
  h.tracker.updateRide({ ...ride, driverLatitude: 15.397 });
  h.tracker.updateRide({ ...ride, driverLatitude: 15.3965 });
  h.tracker.updateRide({ ...ride, driverLatitude: 15.396 });
  await settle();
  assert.equal(h.requests.length, 1, 'GPS does not bypass the 30 second route-query window');
  await h.advance(30000);
  assert.equal(h.requests.length, 2);
  assert.equal(h.requests[1].origin.lat, 15.396);
  assert.equal(h.requests[1].destination.lon, ride.destinationLongitude);
  h.tracker.dispose();
});

test('one failed leg leaves only the successful road path and explicit retry completes both', async () => {
  let failApproach = true;
  const ride = sampleRide();
  const h = harness((origin, destination) => {
    if (destination.lat === ride.pickupLatitude && failApproach) return Promise.reject(new Error('route provider temporarily unavailable'));
    return Promise.resolve(routeResponse(origin, destination));
  });
  h.tracker.updateRide(ride);
  await settle();
  assert.equal(h.state.status, 'partial');
  assert.deepEqual(h.state.legs.map(leg => leg.id), ['trip']);
  assert.ok(h.state.message.length > 0);
  failApproach = false;
  h.tracker.retry();
  await settle();
  assert.equal(h.state.status, 'ready');
  assert.deepEqual(h.state.legs.map(leg => leg.id).sort(), ['approach', 'trip']);
  h.tracker.dispose();
});

test('malformed provider geometry is an error, never a straight line between endpoints', async () => {
  const h = harness(() => Promise.resolve({ encodedPolyline: 'broken provider data' }));
  h.tracker.updateRide(sampleRide({ status: 5 }));
  await settle();
  assert.equal(h.state.status, 'error');
  assert.deepEqual(h.state.legs, []);
  assert.ok(h.state.message.length > 0);
  h.tracker.dispose();
});

test('missing driver coordinates do not send zero-origin requests or invent an approach leg', async () => {
  const h = harness(), ride = sampleRide({ driverLatitude: null, driverLongitude: null });
  h.tracker.updateRide(ride);
  await settle();
  assert.equal(h.requests.length, 1);
  assert.deepEqual(h.requests[0].origin, point(ride, 'pickup'));
  assert.deepEqual(h.state.legs.map(leg => leg.id), ['trip']);
  assert.ok(['partial', 'unavailable'].includes(h.state.status));
  h.tracker.dispose();
});

test('cancellation under review shows only a labelled planned road and ignores current driver movement', async () => {
  const h = harness(), ride = sampleRide({ status: 8 });
  h.tracker.updateRide(ride);
  await settle();
  assert.deepEqual(h.requests, [{ origin: point(ride, 'pickup'), destination: point(ride, 'destination') }]);
  assert.equal(h.state.status, 'ready');
  assert.deepEqual(h.state.legs.map(leg => leg.id), ['planned']);
  assert.match(h.state.message, /الإلغاء قيد المراجعة/);
  assert.match(h.state.message, /الطريق المخطط/);
  assert.match(h.state.message, /لا يمثل حركة السائق الفعلية/);
  const planned = h.state.legs[0].points;
  h.tracker.updateRide({ ...ride, driverLatitude: 15.5, driverLongitude: 44.3 });
  await settle();
  h.tracker.updateRide({ ...ride, driverLatitude: null, driverLongitude: null });
  await settle();
  assert.equal(h.requests.length, 1, 'current driver GPS neither reroutes nor clips the planned road');
  assert.deepEqual(h.state.legs[0].points, planned);
  h.tracker.dispose();
});

test('entering cancellation review and clear reject late live-route responses', async () => {
  const pending = [], h = harness(() => { const item = deferred(); pending.push(item); return item.promise; });
  const ride = sampleRide({ status: 5 });
  h.tracker.updateRide(ride);
  h.tracker.updateRide({ ...ride, status: 8 });
  await settle();
  assert.equal(h.requests.length, 2);
  assert.deepEqual(h.requests[1], { origin: point(ride, 'pickup'), destination: point(ride, 'destination') });
  pending[1].resolve(routeResponse(point(ride, 'pickup'), point(ride, 'destination')));
  await settle();
  assert.deepEqual(h.state.legs.map(leg => leg.id), ['planned']);
  const plannedState = JSON.stringify(h.state);
  pending[0].resolve(routeResponse(point(ride, 'driver'), point(ride, 'destination')));
  await settle();
  assert.equal(JSON.stringify(h.state), plannedState);
  h.tracker.clear();
  assert.equal(h.state.status, 'idle');
  assert.deepEqual(h.state.legs, []);
  assert.equal(h.timers.size, 0);
  h.tracker.dispose();
});

test('an ongoing trip without live GPS uses a labelled planned road and switches cleanly when GPS recovers', async () => {
  const pending = [], h = harness(() => { const item = deferred(); pending.push(item); return item.promise; });
  const live = sampleRide({ status: 5 }), missing = { ...live, driverLatitude: null, driverLongitude: null };
  h.tracker.updateRide(missing);
  await settle();
  assert.deepEqual(h.requests, [{ origin: point(live, 'pickup'), destination: point(live, 'destination') }]);
  assert.match(h.state.message, /موقع السائق الحالي غير متاح/);
  h.tracker.updateRide(live);
  await settle();
  assert.equal(h.requests.length, 2);
  assert.deepEqual(h.requests[1], { origin: point(live, 'driver'), destination: point(live, 'destination') });
  pending[1].resolve(routeResponse(point(live, 'driver'), point(live, 'destination')));
  await settle();
  const liveState = JSON.stringify(h.state);
  assert.deepEqual(h.state.legs.map(leg => leg.id), ['remaining']);
  pending[0].resolve(routeResponse(point(live, 'pickup'), point(live, 'destination')));
  await settle();
  assert.equal(JSON.stringify(h.state), liveState, 'late planned response cannot replace the recovered live route');
  h.tracker.updateRide(missing);
  await settle();
  assert.deepEqual(h.state.legs.map(leg => leg.id), ['planned'], 'losing GPS removes the previous remaining leg');
  assert.equal(h.state.status, 'ready');
  assert.match(h.state.message, /موقع السائق الحالي غير متاح/);
  assert.match(h.state.message, /لا يمثل حركة السائق الفعلية/);
  assert.deepEqual(h.state.legs[0].points[0], [live.pickupLatitude, live.pickupLongitude]);
  assert.equal(h.requests.length, 2, 'valid cached planned geometry is reused');
  h.tracker.dispose();
});

test('losing GPS rejects a pending remaining route while the planned road loads', async () => {
  const pending = [], h = harness(() => { const item = deferred(); pending.push(item); return item.promise; });
  const ride = sampleRide({ status: 5 });
  h.tracker.updateRide(ride);
  await settle();
  h.tracker.updateRide({ ...ride, driverLatitude: '', driverLongitude: '' });
  await settle();
  pending[0].resolve(routeResponse(point(ride, 'driver'), point(ride, 'destination')));
  await settle();
  assert.deepEqual(h.state.legs, []);
  assert.equal(h.state.status, 'loading');
  pending[1].resolve(routeResponse(point(ride, 'pickup'), point(ride, 'destination')));
  await settle();
  assert.deepEqual(h.state.legs.map(leg => leg.id), ['planned']);
  h.tracker.dispose();
});

test('planned routes never fabricate a line on provider failure and explicit retry can recover', async () => {
  for (const overrides of [{ status: 8 }, { status: 5, driverLatitude: null, driverLongitude: null }]) {
    for (const failure of ['rejected', 'malformed']) {
      let fail = true;
      const h = harness((origin, destination) => fail ? failure === 'rejected' ? Promise.reject(new Error('provider unavailable')) :
        Promise.resolve({ encodedPolyline: 'invalid geometry' }) : Promise.resolve(routeResponse(origin, destination)));
      h.tracker.updateRide(sampleRide(overrides));
      await settle();
      assert.equal(h.state.status, 'error');
      assert.deepEqual(h.state.legs, []);
      assert.match(h.state.message, /تعذر حساب الطريق المخطط/);
      fail = false;
      h.tracker.retry();
      await settle();
      assert.equal(h.state.status, 'ready');
      assert.deepEqual(h.state.legs.map(leg => leg.id), ['planned']);
      h.tracker.dispose();
    }
  }
});

test('planned routes require valid requested endpoints and never claim driver arrival for coincident points', async () => {
  for (const overrides of [{ status: 8 }, { status: 5, driverLatitude: null, driverLongitude: null }]) {
    const h = harness();
    h.tracker.updateRide(sampleRide({ ...overrides, pickupLatitude: null }));
    await settle();
    assert.equal(h.requests.length, 0);
    assert.equal(h.state.status, 'unavailable');
    assert.deepEqual(h.state.legs, []);
    h.tracker.updateRide(sampleRide({ ...overrides, destinationLongitude: null }));
    await settle();
    assert.equal(h.requests.length, 0);
    assert.equal(h.state.status, 'unavailable');
    const ride = sampleRide(overrides);
    h.tracker.updateRide({ ...ride, destinationLatitude: ride.pickupLatitude, destinationLongitude: ride.pickupLongitude });
    await settle();
    assert.equal(h.requests.length, 0);
    assert.deepEqual(h.state.legs, []);
    assert.match(h.state.message, /متقاربتان/);
    assert.doesNotMatch(h.state.message, /السائق عند نهاية/);
    h.tracker.dispose();
  }
});

test('terminal ride states and clear still remove planned roads', async () => {
  const h = harness(), ride = sampleRide({ status: 8 });
  h.tracker.updateRide(ride);
  await settle();
  assert.deepEqual(h.state.legs.map(leg => leg.id), ['planned']);
  for (const status of [6, 7]) {
    h.tracker.updateRide({ ...ride, status });
    await settle();
    assert.equal(h.state.status, 'unavailable');
    assert.deepEqual(h.state.legs, []);
  }
  assert.equal(h.requests.length, 1);
  h.tracker.clear();
  assert.equal(h.state.status, 'idle');
  assert.equal(h.timers.size, 0);
  h.tracker.dispose();
});

test('revisiting a recent route uses the cache without extra provider calls', async () => {
  const h = harness(), first = sampleRide({ status: 5 }), second = sampleRide({ id: 102, status: 5,
    driverLatitude: 15.42, destinationLatitude: 15.43 });
  h.tracker.updateRide(first);
  await settle();
  h.tracker.updateRide(second);
  await settle();
  assert.equal(h.requests.length, 2);
  h.tracker.updateRide(first);
  await settle();
  assert.equal(h.requests.length, 2);
  assert.equal(h.state.rideId, first.id);
  assert.equal(h.state.status, 'ready');
  h.tracker.dispose();
});

test('a hanging provider times out, explicit retry recovers, and the late response cannot poison the cache', async () => {
  const pending = [];
  const h = harness(() => { const item = deferred(); pending.push(item); return item.promise; });
  const ride = sampleRide({ status: 5 });
  h.tracker.updateRide(ride);
  await settle();
  assert.equal(h.requests.length, 1);
  await h.advance(20001);
  assert.equal(h.state.status, 'error');
  assert.deepEqual(h.state.legs, []);
  assert.ok(h.state.message.length > 0);
  h.tracker.retry();
  await settle();
  assert.equal(h.requests.length, 2, 'retry starts a new request after the hung one expires');
  const freshPoints = [[ride.driverLatitude, ride.driverLongitude],
    [15.409, ride.driverLongitude], [ride.destinationLatitude, ride.destinationLongitude]];
  pending[1].resolve(response(freshPoints));
  await settle();
  assert.equal(h.state.status, 'ready');
  assert.deepEqual(h.state.legs[0].points, freshPoints);
  pending[0].resolve(routeResponse(point(ride, 'driver'), point(ride, 'destination')));
  await settle();
  assert.deepEqual(h.state.legs[0].points, freshPoints, 'expired response cannot replace the retry result');
  h.tracker.clear();
  h.tracker.updateRide(ride);
  await settle();
  assert.equal(h.requests.length, 2);
  assert.deepEqual(h.state.legs[0].points, freshPoints, 'expired response cannot overwrite the cache either');
  h.tracker.dispose();
});

test('disposing clears timeout work and ignores a pending provider response', async () => {
  const pending = deferred(), h = harness(() => pending.promise);
  const ride = sampleRide({ status: 5 });
  h.tracker.updateRide(ride);
  await settle();
  h.tracker.dispose();
  assert.equal(h.timers.size, 0);
  const countAfterDispose = h.states.length;
  pending.resolve(routeResponse(point(ride, 'driver'), point(ride, 'destination')));
  await settle();
  await h.advance(30000);
  h.tracker.updateRide(ride);
  await settle();
  assert.equal(h.states.length, countAfterDispose);
  assert.equal(h.requests.length, 1);
  assert.equal(h.timers.size, 0);
});

test('an initial provider outage recovers automatically after the retry window', async () => {
  const h = harness((origin, destination, index) => index === 0 ? Promise.reject(new Error('temporary outage')) :
    Promise.resolve(routeResponse(origin, destination)));
  h.tracker.updateRide(sampleRide({ status: 5 }));
  await settle();
  assert.equal(h.state.status, 'error');
  await h.advance(29999);
  assert.equal(h.requests.length, 1);
  await h.advance(1);
  assert.equal(h.requests.length, 2);
  assert.equal(h.state.status, 'ready');
  assert.equal(h.state.legs[0].id, 'remaining');
  h.tracker.dispose();
});

test('clearing or changing the selected ride cancels an obsolete outage retry', async () => {
  const h = harness((origin, destination, index) => index === 0 ? Promise.reject(new Error('temporary outage')) :
    Promise.resolve(routeResponse(origin, destination)));
  h.tracker.updateRide(sampleRide({ status: 5 }));
  await settle();
  assert.equal(h.state.status, 'error');
  h.tracker.updateRide(sampleRide({ id: 102, status: 5, driverLatitude: 15.42, destinationLatitude: 15.43 }));
  await settle();
  await h.advance(30000);
  assert.equal(h.requests.length, 2);
  assert.equal(h.state.rideId, 102);
  assert.equal(h.state.status, 'ready');
  h.tracker.dispose();

  const cleared = harness(() => Promise.reject(new Error('temporary outage')));
  cleared.tracker.updateRide(sampleRide({ status: 5 }));
  await settle();
  cleared.tracker.clear();
  await cleared.advance(30000);
  assert.equal(cleared.requests.length, 1);
  assert.equal(cleared.state.status, 'idle');
  assert.equal(cleared.timers.size, 0);
  cleared.tracker.dispose();
});

test('confirmed off-road recalculation bypasses the cached route for unchanged endpoints', async () => {
  const ride = sampleRide({ status: 5 });
  const h = harness((origin, destination, index) => index === 0 ?
    Promise.resolve(response([[origin.lat + 0.002, origin.lon], [origin.lat + 0.002, destination.lon],
      [destination.lat, destination.lon]])) : Promise.resolve(routeResponse(origin, destination)));
  h.tracker.updateRide(ride);
  await settle();
  assert.equal(h.requests.length, 1);
  await h.advance(30000);
  assert.equal(h.requests.length, 2, 'an unusable cached route cannot satisfy the recalculation');
  assert.deepEqual(h.requests[1], h.requests[0]);
  assert.equal(h.state.status, 'ready');
  assert.deepEqual(h.state.legs[0].points[0], [ride.driverLatitude, ride.driverLongitude]);
  h.tracker.dispose();
});

test('default timer calls preserve the browser Window receiver', async () => {
  const browserWindow = {};
  browserWindow.setTimeout = function (callback, delay) {
    assert.equal(this, browserWindow, 'native Window.setTimeout rejects an unrelated receiver');
    return setTimeout(callback, delay);
  };
  browserWindow.clearTimeout = function (timer) {
    assert.equal(this, browserWindow, 'native Window.clearTimeout rejects an unrelated receiver');
    return clearTimeout(timer);
  };
  const context = vm.createContext({ window: browserWindow,
    setTimeout: browserWindow.setTimeout, clearTimeout: browserWindow.clearTimeout });
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../src/YemenDrive.Api/wwwroot/admin-route-tracker.js'), 'utf8'), context);
  const states = [];
  const tracker = new browserWindow.AdminRouteTracker({
    fetchRoute: async (origin, destination) => routeResponse(origin, destination),
    decodePolyline: JSON.parse,
    onChange: state => states.push(state)
  });
  try {
    tracker.updateRide(sampleRide({ status: 5 }));
    await settle();
    assert.equal(states.at(-1).status, 'ready');
    assert.equal(states.at(-1).legs.length, 1);
  } finally { tracker.dispose(); }
});
