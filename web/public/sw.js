// Marten's service worker: keeps the app shell available so the installed app opens instantly,
// and never caches the API (it's live data behind a token).
const CACHE = 'marten-shell-v1';
const SHELL = ['/', '/manifest.webmanifest', '/icon.svg', '/icon-192.png', '/icon-512.png'];

self.addEventListener('install', event => {
  event.waitUntil(caches.open(CACHE).then(c => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', event => {
  event.waitUntil(caches.keys()
    .then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k))))
    .then(() => self.clients.claim()));
});

self.addEventListener('fetch', event => {
  const url = new URL(event.request.url);
  if (event.request.method !== 'GET' || url.origin !== location.origin || url.pathname.startsWith('/api')) return;
  if (event.request.mode === 'navigate') {
    // Network first, so a rebuilt UI shows up on the next open; the cached shell when Marten is unreachable.
    event.respondWith(fetch(event.request).then(res => {
      const copy = res.clone();
      caches.open(CACHE).then(c => c.put('/', copy));
      return res;
    }).catch(() => caches.match('/')));
    return;
  }
  if (url.pathname.startsWith('/assets/')) {
    // Vite's assets are content-hashed, so a cached copy is always right.
    event.respondWith(caches.match(event.request).then(hit => hit ?? fetch(event.request).then(res => {
      if (res.ok) { const copy = res.clone(); caches.open(CACHE).then(c => c.put(event.request, copy)); }
      return res;
    })));
  }
});
