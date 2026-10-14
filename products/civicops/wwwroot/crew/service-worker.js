'use strict';
const SCOPE=new URL(self.registration.scope).pathname;
const OWNER='civicops-crew-shell-'+encodeURIComponent(SCOPE)+'-';
const CACHE = OWNER+'v2';
const SHELL = [SCOPE,'/crew/crew.css','/crew/crew-core.js','/crew/crew.js','/crew/strings.json','/crew/manifest.json','/crew/icon-192.png','/crew/icon-512.png','/icon.svg'];
self.addEventListener('install', event => event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(SHELL)).then(() => self.skipWaiting())));
self.addEventListener('activate', event => event.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(key => key.startsWith(OWNER) && key !== CACHE).map(key => caches.delete(key)))).then(() => self.clients.claim())));
self.addEventListener('fetch', event => {
  const request = event.request; const url = new URL(request.url);
  if (request.method !== 'GET' || url.origin !== self.location.origin) return;
  // Deliberately no cache path for auth, APIs, photos, tiles or operational data.
  if (request.mode === 'navigate' && [SCOPE.slice(0,-1),SCOPE,SCOPE+'index.html'].includes(url.pathname)) {
    event.respondWith(fetch(request).catch(() => caches.match(SCOPE))); return;
  }
  if (SHELL.includes(url.pathname)) event.respondWith(fetch(request).catch(() => caches.match(url.pathname)));
});
// Foreground pages hold authentication. A service worker never holds a token, requests
// geolocation or submits work on behalf of a hidden/closed page.
self.addEventListener('sync', event => {
  if (event.tag === 'civicops-crew-outbox') event.waitUntil(self.clients.matchAll({type:'window'}).then(clients => clients.forEach(client => client.postMessage({type:'crew:sync-needed'}))));
});
