// The service worker: keeps the app itself on the phone, so it opens with the Mac out of reach (the app shows its
// last lists and chats under Offline), shows each notification Uselim pushes, and a tap opens the chat it names.

// the app's own files: fetched fresh while the Mac answers within a few seconds, the kept copy otherwise
const SHELL = 'uselim-shell'
const SHELL_FILES = [
  '/', '/app.js', '/app.css', '/manifest.webmanifest', '/icon.png', '/icon-192.png',
  '/back.png', '/send.png', '/attach.png', '/remove.png', '/file.png', '/bell.png', '/gear.png', '/reply.png',
  '/question.png', '/permission.png', '/limit.png', '/compose.png', '/stop.png', '/copy.png', '/copied.png',
  '/updown.png', '/fold.png', '/scan.png', '/highlight-11.12.0.min.js', '/jsQR-1.4.0.js',
]
const SHELL_WAIT = 4000
// the chat's pictures, each fixed at its address: the kept copy first, the newest 300 kept
const PICTURES = 'uselim-pictures'
const PICTURES_KEPT = 300

self.addEventListener('install', event => {
  event.waitUntil(
    caches
      .open(SHELL)
      .then(cache => cache.addAll(SHELL_FILES))
      .catch(() => {})
      .then(() => self.skipWaiting()),
  )
})
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()))

self.addEventListener('fetch', event => {
  const request = event.request
  if (request.method !== 'GET') return
  const url = new URL(request.url)
  if (url.origin !== self.location.origin) return
  if (url.pathname === '/api/image') event.respondWith(picture(request))
  else if (request.mode === 'navigate' || SHELL_FILES.includes(url.pathname)) event.respondWith(shell(request, url))
})

// the fresh file, kept for next time; the kept one when the Mac stays silent past SHELL_WAIT or fails
async function shell(request, url) {
  const key = request.mode === 'navigate' ? '/' : url.pathname
  const cache = await caches.open(SHELL)
  const fresh = fetch(request).then(response => {
    if (response.ok) cache.put(key, response.clone())
    return response
  })
  const late = new Promise(resolve => setTimeout(resolve, SHELL_WAIT))
  try {
    const first = await Promise.race([fresh, late])
    if (first?.ok) return first
  } catch {
    // out of reach: the kept copy below
  }
  const kept = await cache.match(key)
  if (kept) return kept
  return fresh
}

async function picture(request) {
  const cache = await caches.open(PICTURES)
  const kept = await cache.match(request.url)
  if (kept) return kept
  const response = await fetch(request)
  if (response.ok) {
    await cache.put(request.url, response.clone())
    const keys = await cache.keys()
    for (const old of keys.slice(0, Math.max(0, keys.length - PICTURES_KEPT))) await cache.delete(old)
  }
  return response
}

self.addEventListener('push', event => {
  let data = {}
  try {
    data = event.data ? event.data.json() : {}
  } catch {
    data = { body: event.data ? event.data.text() : '' }
  }
  event.waitUntil(
    self.registration.showNotification(data.title || 'Claude', {
      body: data.body || '',
      tag: data.tag || undefined,
      data: { url: data.url || '/' },
    }),
  )
})

self.addEventListener('notificationclick', event => {
  event.notification.close()
  const url = new URL(event.notification.data?.url || '/', self.location.origin).href
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(windows => {
      for (const client of windows) {
        if ('focus' in client) {
          client.postMessage({ open: url })
          return client.focus()
        }
      }
      return self.clients.openWindow(url)
    }),
  )
})
