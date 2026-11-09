const { test } = require('node:test');
const assert = require('node:assert/strict');
const { AdminStateSync, acceptLocation } = require('../src/YemenDrive.Api/wwwroot/admin-state-sync.js');
const event = (version, stream = 'ride:1') => ({ stream, version, schemaVersion: 1, resource: 'ride', resourceId: 1 });
test('new event during canonical read requires another read; duplicate never does', async () => {
  let release, started; const entered = new Promise(r => started = r); let reads = 0;
  const sync = new AdminStateSync(async () => { if (++reads === 1) { started(); await new Promise(r => release = r); } });
  sync.receive(event(1)); const work = sync.drain(); await entered;
  sync.receive(event(2)); release(); await work;
  assert.equal(reads, 2); assert.equal(sync.applied.get('ride:1'), 2);
  assert.equal(sync.receive(event(1)), false); assert.equal(sync.receive(event(2)), false);
});
test('failed read stays pending for recovery, and logout rejects late acknowledgements', async () => {
  const sync = new AdminStateSync(async () => { throw new Error('offline'); });
  sync.receive(event(1)); await assert.rejects(sync.drain()); assert.equal(sync.applied.size, 0);
  let release, started; const entered = new Promise(r => started = r);
  sync.refresh = async () => { started(); await new Promise(r => release = r); };
  const work = sync.drain(); await entered; sync.disconnect(true); release(); await work;
  assert.equal(sync.applied.size, 0); assert.equal(sync.pending.size, 0);
});
test('keyset replay covers more than 512 streams with bounded acknowledgements', async () => {
  let reads = 0, pages = 0;
  const sync = new AdminStateSync(async batch => reads += batch.length);
  await sync.recover(async (_, cursor) => {
    pages++; const start = cursor ? Number(cursor.split(':')[1]) + 1 : 0;
    const events = Array.from({ length: Math.min(128, 650 - start) }, (_, i) => event(1, `ride:${String(start + i).padStart(4, '0')}`));
    return { supported: true, events, hasMore: start + events.length < 650, nextStream: events.at(-1).stream };
  });
  assert.equal(reads, 650); assert.equal(pages, 6); assert.equal(sync.applied.size, 512);
});
test('old, duplicated, impossible and future GPS cannot move the marker backwards', () => {
  const now = Date.now(); const previous = { latitude: 15, longitude: 44, observedAtUtc: new Date(now - 2000).toISOString() };
  assert.equal(acceptLocation(previous, { ...previous }), false);
  assert.equal(acceptLocation(previous, { ...previous, latitude: 16, observedAtUtc: new Date(now).toISOString() }), false);
  assert.equal(acceptLocation(previous, { ...previous, observedAtUtc: new Date(now + 10000).toISOString() }), false);
  assert.equal(acceptLocation(previous, { ...previous, latitude: 15.0001, observedAtUtc: new Date(now).toISOString() }), true);
});
test('unknown event schema cannot be acknowledged as recovered', async () => {
  const sync = new AdminStateSync(async () => {});
  await assert.rejects(sync.recover(async () => ({ supported: true, events: [{ ...event(1), schemaVersion: 2 }], hasMore: false })));
  assert.equal(sync.applied.size, 0);
});
test('concurrent recovery shares one replay and malformed replay never synchronizes', async () => {
  let release, calls = 0; const statuses = [];
  const sync = new AdminStateSync(async () => {}, value => statuses.push(value));
  const read = async () => { calls++; await new Promise(r => release = r); return { supported: true, events: [], hasMore: false }; };
  const first = sync.recover(read); const second = sync.recover(read);
  assert.equal(first, second); release(); await first; assert.equal(calls, 1);
  await assert.rejects(sync.recover(async () => ({ supported: true, hasMore: false })));
  assert.equal(statuses.at(-1), false);
});
