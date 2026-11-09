/* Durable invalidations are acknowledged only after their canonical reads finish. */
(function (root) {
  class AdminStateSync {
    constructor(refresh, status = () => {}) {
      this.refresh = refresh; this.status = status;
      this.applied = new Map(); this.pending = new Map(); this.epoch = 0;
      this.running = null; this.recovering = false; this.supported = false;
      this.recoveryTask = null; this.recoveryEpoch = -1;
    }
    receive(event) {
      if (!event || event.schemaVersion !== 1 || typeof event.stream !== 'string' || !event.stream || event.stream.length > 120 ||
          typeof event.resource !== 'string' || !Number.isSafeInteger(event.resourceId) || event.resourceId < 0 ||
          !Number.isSafeInteger(event.version) || event.version < 1)
        throw new Error('Unsupported state event');
      if (event.version <= (this.applied.get(event.stream) || 0) ||
          event.version <= (this.pending.get(event.stream)?.version || 0)) return false;
      this.supported = true;
      this.pending.set(event.stream, event); this.status(false);
      return true;
    }
    drain() {
      if (this.running) return this.running;
      const epoch = this.epoch;
      const work = async () => {
        while (this.pending.size && epoch === this.epoch) {
          const batch = [...this.pending.values()];
          await this.refresh(batch);
          if (epoch !== this.epoch) return;
          for (const event of batch) {
            this.applied.delete(event.stream); this.applied.set(event.stream, event.version);
            if (this.pending.get(event.stream)?.version === event.version) this.pending.delete(event.stream);
          }
          while (this.applied.size > 512) this.applied.delete(this.applied.keys().next().value);
        }
        if (epoch === this.epoch && !this.recovering) this.status(true);
      };
      this.running = Promise.resolve().then(work).finally(() => { this.running = null; });
      return this.running;
    }
    recover(readPage) {
      if (this.recoveryTask && this.recoveryEpoch === this.epoch) return this.recoveryTask;
      this.recoveryEpoch = this.epoch;
      const task = this._recover(readPage).finally(() => { if (this.recoveryTask === task) this.recoveryTask = null; });
      this.recoveryTask = task;
      return task;
    }
    async _recover(readPage) {
      const epoch = this.epoch;
      this.recovering = true; this.status(false);
      try {
        let cursor = '';
        do {
          const page = await readPage(Object.fromEntries(this.applied), cursor);
          if (epoch !== this.epoch) return;
          this.supported = page?.supported === true;
          if (!this.supported) return;
          if (!Array.isArray(page.events)) throw new Error('Invalid state replay events');
          for (const event of page.events) this.receive(event);
          await this.drain();
          if (epoch !== this.epoch) return;
          if (!page.hasMore) break;
          if (typeof page.nextStream !== 'string' || page.nextStream <= cursor) throw new Error('Invalid state replay cursor');
          cursor = page.nextStream;
        } while (true);
      } finally { if (epoch === this.epoch) this.recovering = false; }
      if (epoch === this.epoch) await this.drain();
    }
    disconnect(reset = false) {
      this.epoch++; this.recovering = false; this.status(false);
      if (reset) { this.applied.clear(); this.pending.clear(); this.supported = false; }
    }
  }
  function acceptLocation(previous, sample, now = Date.now()) {
    const at = Date.parse(sample?.observedAtUtc);
    const before = Date.parse(previous?.observedAtUtc);
    if (!Number.isFinite(at) || at > now + 5000 || at <= before ||
        !Number.isFinite(sample?.latitude) || Math.abs(sample.latitude) > 90 ||
        !Number.isFinite(sample?.longitude) || Math.abs(sample.longitude) > 180) return false;
    if (Number.isFinite(before) && Number.isFinite(previous.latitude) && Number.isFinite(previous.longitude)) {
      const rad = Math.PI / 180;
      const dLat = (sample.latitude - previous.latitude) * rad, dLon = (sample.longitude - previous.longitude) * rad;
      const a = Math.sin(dLat / 2) ** 2 + Math.cos(previous.latitude * rad) * Math.cos(sample.latitude * rad) * Math.sin(dLon / 2) ** 2;
      const meters = 6371000 * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(Math.max(0, 1 - a)));
      if (meters > 75 * (at - before) / 1000 + 100) return false;
    }
    return true;
  }
  root.AdminStateSync = AdminStateSync; root.acceptLiveLocation = acceptLocation;
  if (typeof module !== 'undefined') module.exports = { AdminStateSync, acceptLocation };
})(typeof window === 'undefined' ? globalThis : window);
