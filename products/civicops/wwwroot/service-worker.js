'use strict';
const CACHE = 'civicops-shell-v12';
const SHELL = ['/', '/app.css', '/app.js', '/map.css', '/map.js', '/channels.css', '/channels.js', '/vendor/leaflet-1.9.4/leaflet.css', '/vendor/leaflet-1.9.4/leaflet.js', '/vendor/leaflet.markercluster-1.5.3/MarkerCluster.css', '/vendor/leaflet.markercluster-1.5.3/leaflet.markercluster.js', '/i18n.js', '/locales/en.json', '/locales/af.json', '/locales/zu.json', '/admin.js', '/crew-devices.js', '/icon.svg', '/media/culltron-mark-72.webp', '/manifest.json', '/offline.html'];
self.addEventListener('install', event => event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(SHELL)).then(() => self.skipWaiting())));
self.addEventListener('activate', event => event.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(key => key.startsWith('civicops-shell-') && key !== CACHE).map(key => caches.delete(key)))).then(() => self.clients.claim())));
self.addEventListener('fetch', event => {
  const request = event.request; const url = new URL(request.url);
  if(request.method !== 'GET' || url.origin !== self.location.origin) return;
  if(/^\/(?:m\/[^/]+\/)?(?:api|auth|open311)(?:\/|$)/.test(url.pathname) || url.pathname === '/healthz') return;
  if(request.mode === 'navigate') event.respondWith(fetch(request).catch(() => caches.match('/offline.html')));
  else if(SHELL.includes(url.pathname)) event.respondWith(fetch(request).catch(() => caches.match(url.pathname)));
});
