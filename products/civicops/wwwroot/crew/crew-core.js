'use strict';
// Offline storage contains work evidence, never authentication or worker location samples.
(() => {
  const JOB_TTL = 24 * 60 * 60 * 1000;
  const DRAFT_TTL = 7 * 24 * 60 * 60 * 1000;
  class CrewDatabase {
    constructor() { this.opening = null; }
    open() {
      if (!this.opening) this.opening = new Promise((resolve, reject) => {
        const request = indexedDB.open('civicops-crew-v1', 1);
        request.onupgradeneeded = () => { request.result.createObjectStore('cache', {keyPath:'id'}); request.result.createObjectStore('outbox', {keyPath:'id'}); };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
        request.onblocked = () => reject(new Error('Close another crew tab to update offline storage.'));
      });
      return this.opening;
    }
    async transaction(store, mode, action, valid = () => true) {
      const db = await this.open();
      if (!valid()) { const error = new Error('Identity changed; stale offline transaction ignored.'); error.stale = true; throw error; }
      return new Promise((resolve, reject) => {
        const transaction = db.transaction(store, mode); let value;
        const request = action(transaction.objectStore(store));
        request.onsuccess = () => { value = request.result; };
        transaction.oncomplete = () => resolve(value);
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(transaction.error || new Error('Offline storage transaction was interrupted.'));
      });
    }
    get(id) { return this.transaction('cache','readonly',store => store.get(id)); }
    put(value, valid) { return this.transaction('cache','readwrite',store => store.put(value), valid); }
    add(value, valid) { return this.transaction('outbox','readwrite',store => store.put(value), valid); }
    remove(id, valid) { return this.transaction('outbox','readwrite',store => store.delete(id), valid); }
    async list(partition) { return (await this.transaction('outbox','readonly',store => store.getAll())).filter(item => item.partition === partition).sort((a,b) => a.createdAt-b.createdAt || a.id.localeCompare(b.id)); }
    async clear(valid = () => true) {
      const db = await this.open();
      if (!valid()) { const error = new Error('Identity changed; stale clear ignored.'); error.stale = true; throw error; }
      await new Promise((resolve,reject) => { const tx=db.transaction(['cache','outbox'],'readwrite');tx.objectStore('cache').clear();tx.objectStore('outbox').clear();tx.oncomplete=resolve;tx.onerror=()=>reject(tx.error);tx.onabort=()=>reject(tx.error); });
    }
    async purge(now = Date.now()) {
      const db=await this.open();let removed=0;
      return new Promise((resolve,reject)=>{
        // Inspect and delete within one transaction so a refreshed record cannot be removed by an old purge snapshot.
        const tx=db.transaction(['cache','outbox'],'readwrite');
        for(const name of ['cache','outbox']) { const request=tx.objectStore(name).openCursor();request.onsuccess=()=>{const cursor=request.result;if(!cursor)return;if(cursor.value.expiresAt<=now) { cursor.delete();removed++; }cursor.continue();}; }
        tx.oncomplete=()=>resolve(removed);tx.onerror=()=>reject(tx.error);tx.onabort=()=>reject(tx.error);
      });
    }
  }

  class LocationGate {
    constructor(now = () => Date.now()) { this.now = now; this.requested = false; this.authoritative = false; this.visible = false; this.online = false; this.shift = null; this.consent = null; }
    allowed() { return this.requested && this.authoritative && this.visible && this.online && !!this.shift && !this.shift.endedAt && Date.parse(this.shift.expiresAt) > this.now() && !!this.consent && !this.consent.withdrawnAt; }
    stop() { this.requested = false; }
  }
  function retryDelay(attempt, jitter = Math.random()) { return Math.min(300000, 2000 * 2 ** Math.min(8, Math.max(0, attempt - 1))) + Math.round(Math.max(0,Math.min(1,jitter))*1000); }
  window.CivicCrewCore = Object.freeze({CrewDatabase,LocationGate,retryDelay,JOB_TTL,DRAFT_TTL});
})();
