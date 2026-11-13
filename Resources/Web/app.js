// Uselim on the phone: the chats open in VS Code with their replies, questions and permission asks, and the usage
// limits, read from Uselim's relay on the Mac. A message typed here enters its chat as a typed prompt.

// highlight.js 11.12.0 (BSD-3, highlight.LICENSE.txt), with its common languages
import hljs from '/highlight-11.12.0.min.js'

// this phone's own key from the Mac, kept on the phone and sent with every call. The six digits from Pair a phone pair
// once and trade for it; so does the lasting code from before the phones' own keys, by itself, while the calls go on
// with it meanwhile. The QR code's link carries the digits in its fragment, which never leaves the phone: they fill the
// pairing field, so a Safari tab opened from the Camera on the way to the Home Screen spends nothing.
const PAIRING = 'uselimPairing'
let pairing = ''
try {
  pairing = localStorage.getItem(PAIRING) ?? ''
} catch {
  // a phone that keeps nothing asks for the code again on its next start
}
const isSixDigits = code => /^\d{6}$/.test(code)
let linkedCode = ''
{
  const linked = /^#pair=(\d{6})$/.exec(location.hash)
  if (linked) {
    linkedCode = linked[1]
    history.replaceState(null, '', location.pathname + location.search)
  }
}

function keep(key) {
  pairing = key
  try {
    if (key) localStorage.setItem(PAIRING, key)
    else localStorage.removeItem(PAIRING)
  } catch {
    // kept for this visit alone
  }
}
let isUnpaired = false // the Mac asked for the code: the polls rest until it is entered
const headers = () => ({ 'X-Uselim': '1', 'X-Uselim-Key': pairing })
const byId = id => document.getElementById(id)

// every file goes, by path: the chat opens it with its own tools, the way a path pasted at the Mac goes. A picture
// shows as a thumbnail and travels fitted; HEIC and other formats arrive as a JPEG.
const PICTURE_EXTENSIONS = new Set(['jpg', 'jpeg', 'png', 'gif', 'webp', 'heic', 'heif'])
const UPLOAD_LIMIT = 200 * 1024 * 1024
const LONG_SIDE = 1500
// the line the Mac puts between a message's words and the paths of its files
const ATTACHED = 'Attached from the phone:'
// the New chat screen's place in the address, until the Mac's fresh tab gives the chat its own
const NEW = 'new'
// the New chat screen's heading, and the line when the Mac's tab takes past 30 s
const NEW_TITLE = 'New chat'
const STILL_OPENING = 'Chat still opening. Your message is saved here.'
// the switch for a chat's model and effort: the chat's own /model and /effort, this chat alone. The models
// by alias, each the newest of its family; the levels in the panel's own words. Fable stays off the list: it bills
// usage credits.
const MODELS = [
  { alias: 'opus', name: 'Opus' },
  { alias: 'sonnet', name: 'Sonnet' },
  { alias: 'haiku', name: 'Haiku' },
]
const EFFORTS = [
  { level: 'low', name: 'Low' },
  { level: 'medium', name: 'Medium' },
  { level: 'high', name: 'High' },
  { level: 'xhigh', name: 'Extra high' },
  { level: 'max', name: 'Max' },
]
// the sheet's line while a turn runs, since the chat switches between turns
const SWITCH_LATER = 'Switches once this turn ends'

let view = 'list' // the screen showing: list, notices, settings or chat
let state = null // the last /api/state
let notices = null // the last /api/notices: every notice, newest first, each unread until opened
let noticesKey = '' // what the Notifications screen last drew
let phone = 'off' // this phone's push: unavailable, blocked, off or on
let pushNote = '' // a failed turn-on, in place of the main switch's line
let isSwitching = false
let chat = null // the last /api/chat
let chatKey = '' // what the chat view last drew, so a poll redraws only on change
let session = '' // the open chat, from the address's hash
let askId = '' // the ask the card shows
let drafts = {} // picks in the ask card, by question
let isBusy = false
let isAgain = false // a refresh asked for while one ran
let isReached = false // the Mac answered since the page opened
let pending = [] // messages sent from here that the chat has yet to show: { key, session, text, names, id }
let isOpening = false // a New chat waits on the Mac's fresh tab
let sentCount = 0
let stopAt = 0 // when Stop was pressed, so the pill reads Stopping until the turn ends
let isScrollDue = false // a message just went: the view moves to the end
let picked = [] // the files for the next message: { file, url }, a thumbnail address for each picture
let pickNote = '' // the line naming files kept on the phone
let opened = new Set() // tool rows and summaries opened to their details, by id
let wanted = null // a switch asked from here that the chat has yet to take: { session, model, effort, at }
// the chat before the mod's window, page by page from the transcript: cursor undefined until the first page, null at
// the chat's first words
let earlier = { items: [], cursor: undefined, isLoading: false }
const pictures = new Map() // picture address on the Mac to a promise of its local blob address

function isPicture(file) {
  const type = (file.type || '').toLowerCase()
  const ext = file.name.toLowerCase().split('.').pop()
  return (type.startsWith('image/') && type !== 'image/svg+xml') || (!type && PICTURE_EXTENSIONS.has(ext))
}

const isPictureName = name => PICTURE_EXTENSIONS.has(name.toLowerCase().split('.').pop())

// a picture from the Mac, fetched once with the relay's header and kept as a local address
function picture(path) {
  if (!pictures.has(path)) {
    pictures.set(
      path,
      fetch(path, { headers: headers() }).then(response => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`)
        return response.blob().then(blob => URL.createObjectURL(blob))
      }),
    )
  }
  return pictures.get(path)
}

// a thumbnail that fills in once its picture arrives; a tap opens it across the screen
function thumb(path) {
  const node = el('img', 'shot')
  // VoiceOver reads a thumbnail as a button that opens its picture
  node.alt = 'Picture'
  node.setAttribute('role', 'button')
  // a view that sat at the end stays there as the picture takes its room
  node.addEventListener('load', () => {
    const box = byId('messages')
    if (box.scrollHeight - box.scrollTop - box.clientHeight - node.offsetHeight < 120) box.scrollTop = box.scrollHeight
  })
  picture(path).then(
    url => {
      node.src = url
    },
    () => node.remove(),
  )
  node.addEventListener('click', () => {
    picture(path).then(url => {
      byId('viewerPicture').src = url
      resetZoom()
      byId('viewer').hidden = false
      // VoiceOver lands on the viewer's Close, its way out by a double tap
      byId('viewerClose').focus()
    })
  })
  return node
}

function closeViewer() {
  byId('viewer').hidden = true
  resetZoom()
}

// the open picture: two fingers pinch it between 1x and 5x, one finger moves a picture bigger than 1x, a double tap
// zooms to 2.5x around the spot it lands on and back to 1x, and a single tap at 1x closes it
const zoom = { scale: 1, x: 0, y: 0 }
const fingers = new Map()
let gesture = null
let press = null
let lastTap = null
let closing = 0

function drawZoom(settle) {
  const node = byId('viewerPicture')
  node.classList.toggle('settle', settle)
  node.style.transform = `translate(${zoom.x}px, ${zoom.y}px) scale(${zoom.scale})`
}

function resetZoom() {
  Object.assign(zoom, { scale: 1, x: 0, y: 0 })
  fingers.clear()
  gesture = null
  press = null
  lastTap = null
  clearTimeout(closing)
  drawZoom(false)
}

// the picture's centre on the screen at 1x; offsets ignore the transform, and the viewer covers the screen from 0, 0
function pictureCentre() {
  const node = byId('viewerPicture')
  return { x: node.offsetLeft + node.offsetWidth / 2, y: node.offsetTop + node.offsetHeight / 2 }
}

// a picture wider or taller than the screen keeps its edges at the screen's edges; a smaller one stays centred
function clampZoom() {
  const view = byId('viewer')
  const node = byId('viewerPicture')
  zoom.scale = Math.min(5, Math.max(1, zoom.scale))
  if (zoom.scale < 1.05) Object.assign(zoom, { scale: 1, x: 0, y: 0 })
  const spareX = Math.max(0, (node.offsetWidth * zoom.scale - view.clientWidth) / 2)
  const spareY = Math.max(0, (node.offsetHeight * zoom.scale - view.clientHeight) / 2)
  zoom.x = Math.min(spareX, Math.max(-spareX, zoom.x))
  zoom.y = Math.min(spareY, Math.max(-spareY, zoom.y))
}

// each change in the number of fingers starts the gesture again from where the picture stands
function beginGesture() {
  const points = [...fingers.values()]
  if (points.length >= 2) {
    const [a, b] = points
    gesture = {
      pinch: true,
      distance: Math.hypot(a.x - b.x, a.y - b.y) || 1,
      mid: { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 },
      centre: pictureCentre(),
      ...zoom,
    }
  } else if (points.length === 1) {
    gesture = { pinch: false, start: points[0], ...zoom }
  } else {
    gesture = null
  }
}

// a pinch keeps the spot between the fingers under them: the picture's point there stays put as the scale changes
function moveGesture() {
  const points = [...fingers.values()]
  if (gesture?.pinch && points.length >= 2) {
    const [a, b] = points
    const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }
    const scale = Math.min(6, Math.max(0.6, (gesture.scale * Math.hypot(a.x - b.x, a.y - b.y)) / gesture.distance))
    const ratio = scale / gesture.scale
    zoom.scale = scale
    zoom.x = mid.x - gesture.centre.x - ratio * (gesture.mid.x - gesture.centre.x - gesture.x)
    zoom.y = mid.y - gesture.centre.y - ratio * (gesture.mid.y - gesture.centre.y - gesture.y)
    drawZoom(false)
  } else if (gesture && !gesture.pinch && points.length === 1 && zoom.scale > 1) {
    zoom.x = gesture.x + points[0].x - gesture.start.x
    zoom.y = gesture.y + points[0].y - gesture.start.y
    drawZoom(false)
  }
}

// a touch within 350 ms of a tap and near it is the double tap's second: it holds the close the first one started
function isSecondTap(point) {
  return lastTap !== null && Date.now() - lastTap.at < 350 && Math.hypot(point.x - lastTap.x, point.y - lastTap.y) < 40
}

function tapViewer(point, isSecond) {
  if (isSecond) {
    lastTap = null
    if (zoom.scale > 1) {
      Object.assign(zoom, { scale: 1, x: 0, y: 0 })
    } else {
      const centre = pictureCentre()
      Object.assign(zoom, { scale: 2.5, x: (point.x - centre.x) * -1.5, y: (point.y - centre.y) * -1.5 })
      clampZoom()
    }
    drawZoom(true)
    return
  }
  lastTap = { ...point, at: Date.now() }
  if (zoom.scale > 1) return
  closing = setTimeout(closeViewer, 350)
}

// a picture the mod saved goes by its key; one on an earlier page by its place in the transcript ("@line-block")
const imageKey = key =>
  key.startsWith('@')
    ? `/api/image?session=${encodeURIComponent(session)}&at=${encodeURIComponent(key.slice(1))}`
    : `/api/image?session=${encodeURIComponent(session)}&key=${encodeURIComponent(key)}`
const imageFile = name => `/api/image?session=${encodeURIComponent(session)}&file=${encodeURIComponent(name)}`

async function call(path, body) {
  const init =
    body === undefined
      ? { headers: headers() }
      : { method: 'POST', headers: { ...headers(), 'Content-Type': 'application/json' }, body: JSON.stringify(body) }
  const response = await fetch(path, init)
  // a key the Mac turned down: the phone's own (removed on the Mac), or digits spent or past their days
  if (response.status === 401) showPairing(!pairing ? '' : isSixDigits(pairing) ? SPENT : REMOVED)
  if (response.ok && isSixDigits(pairing)) pairWith(pairing)
  if (response.status === 429) {
    // this phone sent too many wrong codes: the Mac answers again once its pause ends
    const wait = (await response.json().catch(() => ({}))).wait ?? 60
    const at = new Date(Date.now() + wait * 1000).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    showPairing(PAUSED(at))
  }
  if (!response.ok) throw new Error(`HTTP ${response.status}`)
  return response.json()
}

// MARK: pairing: the six digits from the Mac's Pair a phone, traded once for this phone's own key

function showPairing(note = '') {
  byId('pairNote').textContent = note
  if (isUnpaired) return
  isUnpaired = true
  for (const name of VIEWS) byId(name).hidden = true
  byId('pair').hidden = false
  byId('pairCode').value = ''
}

// the pairing lines: the code entered differs from the Mac's, digits spent or past their minutes, this phone removed
// on the Mac, the pause after too many wrong codes, and the camera turned off for Uselim
const PAIR_AGAIN = 'Check the code in Pair a phone… on the Mac and enter it again.'
const SPENT = 'Each code pairs one phone within ten minutes. Pair a phone… on the Mac shows a fresh one.'
const REMOVED = 'This phone was removed on the Mac. Pair it again with a fresh code from Pair a phone…'
const PAUSED = at => `Too many codes tried. The Mac takes the next one at ${at}.`
const CAMERA_OFF = 'The camera is off for Uselim: turn it on in Settings, or type the six digits.'

let isTrading = false

// digits from Pair a phone, typed or scanned, or the lasting code from before: traded once for this phone's own key,
// and the chats open
async function pairWith(code) {
  if (isTrading) return
  isTrading = true
  try {
    const response = await fetch('/api/pair', { method: 'POST', headers: { 'X-Uselim': '1', 'X-Uselim-Key': code } })
    if (response.ok) {
      keep((await response.json()).token)
      if (isUnpaired) {
        isUnpaired = false
        byId('pair').hidden = true
        openView()
      }
      return
    }
    if (response.status === 429) {
      const wait = (await response.json().catch(() => ({}))).wait ?? 60
      const at = new Date(Date.now() + wait * 1000).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
      return showPairing(PAUSED(at))
    }
    // digits spent or past their minutes, or a code the Mac knows nothing of
    const reason = (await response.json().catch(() => ({}))).reason
    if (code === pairing) keep('')
    showPairing(reason === 'spent' ? SPENT : PAIR_AGAIN)
  } catch (error) {
    // the Mac out of reach: the code stays for the next try
    if (isUnpaired) byId('pairNote').textContent = `Pairing failed: ${error.message}`
  } finally {
    isTrading = false
  }
}

function takeCode(code) {
  pairWith(code)
}

byId('pairForm').addEventListener('submit', event => {
  event.preventDefault()
  const code = byId('pairCode').value.replace(/\D/g, '')
  if (isSixDigits(code)) takeCode(code)
})

// MARK: the scan: the phone's camera, inside the app, reads the Pair window's QR code and pairs this very app

let scanStream = null
let scanTimer = 0

// jsQR 1.4.0 (Apache-2.0, jsQR.LICENSE.txt), loaded the first time the camera opens
function loadDecoder() {
  if (window.jsQR) return Promise.resolve()
  return new Promise((done, failed) => {
    const script = document.createElement('script')
    script.src = '/jsQR-1.4.0.js'
    script.onload = done
    script.onerror = failed
    document.head.append(script)
  })
}

// the six digits in a scanned code: the Pair window's link, or the digits alone
function codeIn(text) {
  const linked = /#pair=(\d{6})(?!\d)/.exec(text)
  if (linked) return linked[1]
  const digits = text.replace(/\s/g, '')
  return isSixDigits(digits) ? digits : ''
}

async function startScan() {
  byId('pairNote').textContent = ''
  try {
    await loadDecoder()
    scanStream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' }, audio: false })
  } catch {
    byId('pairNote').textContent = CAMERA_OFF
    return
  }
  const video = byId('scanVideo')
  video.srcObject = scanStream
  byId('scanner').hidden = false
  // VoiceOver moves into the camera's screen, onto its way back
  byId('scanCancel').focus()
  await video.play().catch(() => {})
  const canvas = document.createElement('canvas')
  const context = canvas.getContext('2d', { willReadFrequently: true })
  const look = () => {
    if (!scanStream) return
    if (video.readyState >= 2 && video.videoWidth) {
      // the middle square, at most 640 px, keeps each read quick
      const side = Math.min(video.videoWidth, video.videoHeight)
      const size = Math.min(side, 640)
      canvas.width = size
      canvas.height = size
      context.drawImage(video, (video.videoWidth - side) / 2, (video.videoHeight - side) / 2, side, side, 0, 0, size, size)
      const found = window.jsQR(context.getImageData(0, 0, size, size).data, size, size, { inversionAttempts: 'dontInvert' })
      const code = found ? codeIn(found.data) : ''
      if (code) {
        stopScan()
        takeCode(code)
        return
      }
    }
    scanTimer = setTimeout(look, 150)
  }
  look()
}

function stopScan() {
  clearTimeout(scanTimer)
  for (const track of scanStream?.getTracks() ?? []) track.stop()
  scanStream = null
  byId('scanVideo').srcObject = null
  byId('scanner').hidden = true
  if (!byId('pair').hidden) byId('pairScan').focus()
}

byId('pairScan').addEventListener('click', startScan)
byId('scanCancel').addEventListener('click', stopScan)
// the camera closes when the app leaves the screen
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'hidden' && scanStream) stopScan()
})

function el(tag, className = '', text) {
  const node = document.createElement(tag)
  if (className) node.className = className
  if (text !== undefined) node.textContent = text
  return node
}

function button(label, className, onClick) {
  const node = el('button', className, label)
  node.type = 'button'
  node.addEventListener('click', onClick)
  return node
}

// the loading bar: orange while loading, grey while the Mac is out of reach
const bar = mode => {
  byId('bar').className = `bar ${mode}`
}

// MARK: words for time

function short(ms) {
  const minutes = Math.floor(Math.max(0, ms) / 60000)
  if (minutes < 1) return 'now'
  if (minutes < 60) return `${minutes} min`
  const hours = Math.floor(minutes / 60)
  return hours < 24 ? `${hours} h` : `${Math.floor(hours / 24)} d`
}

// a ring's time to its reset within a day: "2HR 23MIN", "23MIN"; past a day the reset itself, "FRI 04:59" in the
// phone's clock style, since "1DAY 11HR" reads as a day for a reset a day and a half away
function left(resets) {
  const minutes = Math.max(0, Math.round((resets - state.now) / 60000))
  if (minutes >= 1440) {
    const at = new Date(resets)
    const day = at.toLocaleDateString(undefined, { weekday: 'short' })
    const time = at.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })
    return `${day} ${time}`.toUpperCase()
  }
  const hours = Math.floor(minutes / 60)
  const rest = minutes % 60
  return hours > 0 ? `${hours}HR ${rest}MIN` : `${rest}MIN`
}

// Uselim's lines: orange from 60%, red from 80%, orange for any severity word past normal
function tone(limit) {
  if (limit.percent >= 80) return 'critical'
  if (limit.percent >= 60 || (limit.severity && limit.severity !== 'normal')) return 'warning'
  return ''
}

// MARK: the list

// "Weekly (7 days)" reads Weekly on its ring
const shortName = name => name.replace(/\s*\([^)]*\)\s*$/, '')
const CIRCLE = 2 * Math.PI * 35

// a ring per limit, its share inside, its name and time left beneath; redrawn as a figure or a minute changes
function drawLimits() {
  const box = byId('limits')
  const limits = state?.limits ?? []
  const isStale = !state?.limitsAt || state.now - state.limitsAt > 600000
  const minutes = limit => (limit.resets ? Math.round((limit.resets - state.now) / 60000) : 0)
  const key = JSON.stringify([limits.map(l => [l.name, l.percent, l.severity, minutes(l)]), isStale])
  if (box.dataset.key === key) return
  box.dataset.key = key
  box.replaceChildren()
  if (limits.length === 0) {
    box.append(el('div', 'empty', 'Limits arrive with the next read on the Mac'))
    return
  }
  const grid = el('div', 'rings')
  grid.style.gridTemplateColumns = `repeat(${Math.min(3, limits.length)}, 1fr)`
  for (const limit of limits) grid.append(ring(limit, isStale))
  box.append(grid)
}

function ring(limit, isStale) {
  const color = isStale ? '' : tone(limit)
  const share = Math.min(100, Math.max(0, limit.percent))
  const node = el('div', 'ring')
  const figure = el('div', 'figure')
  const circle = 'cx="42" cy="42" r="35" fill="none" stroke-width="9"'
  // The round ends reach half the stroke past each end of the dash, so the dash is a stroke shorter and starts half a
  // stroke in: the arc then ends at its true share, and 98% keeps its gap. A full ring is one plain circle.
  const length = share >= 100 ? CIRCLE : Math.max(0, (share / 100) * CIRCLE - 9)
  const arc = `stroke-dasharray="${length.toFixed(1)} ${CIRCLE.toFixed(1)}"` + (share >= 100 ? '' : ' stroke-dashoffset="-4.5"')
  figure.innerHTML =
    `<svg viewBox="0 0 84 84" aria-hidden="true"><circle class="track" ${circle}/>` +
    (share > 0 ? `<circle class="value ${color}" ${circle} ${arc}/>` : '') +
    '</svg>'
  figure.append(el('b', isStale ? 'stale' : color, `${limit.percent}%`))
  node.append(figure, el('div', 'label', shortName(limit.name)))
  if (limit.resets) node.append(el('div', 'left', left(limit.resets)))
  // VoiceOver reads the ring as one line: "Session, 42%, 2 hours 23 minutes left"
  node.setAttribute('role', 'img')
  node.setAttribute('aria-label', [shortName(limit.name), `${limit.percent}%`, limit.resets && spokenLeft(limit.resets)].filter(Boolean).join(', '))
  return node
}

// the time to a reset in words: "2 hours 23 minutes left", or past a day the reset itself, "resets Friday 04:59"
function spokenLeft(resets) {
  const minutes = Math.max(0, Math.round((resets - state.now) / 60000))
  if (minutes >= 1440) {
    const at = new Date(resets)
    const day = at.toLocaleDateString(undefined, { weekday: 'long' })
    return `resets ${day} ${at.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}`
  }
  const hours = Math.floor(minutes / 60)
  const rest = minutes % 60
  const unit = (count, word) => `${count} ${word}${count === 1 ? '' : 's'}`
  return `${hours > 0 ? `${unit(hours, 'hour')} ` : ''}${unit(rest, 'minute')} left`
}

// the bell's count: notices yet to show on the Notifications screen
function drawBadge() {
  const count = state?.unread ?? 0
  const badge = byId('badge')
  badge.hidden = count === 0
  badge.textContent = count > 99 ? '99+' : String(count)
  byId('openNotices').setAttribute('aria-label', count > 0 ? `Notifications, ${count} unread` : 'Notifications')
}

// waiting chats first, then working ones, then the latest reply
function order(a, b) {
  const rank = c => (c.ask ? 0 : c.status === 'working' ? 1 : 2)
  const recent = c => c.reply?.at ?? c.at ?? 0
  return rank(a) - rank(b) || recent(b) - recent(a)
}

// a pulse keeps to the clock's beat, so a dot drawn again carries on mid-pulse in place of flashing back to full
function inPhase(node) {
  node.style.animationDelay = `${-(Date.now() % 1400)}ms`
  return node
}

// MARK: a row's swipe, Mail's way: a swipe left shows Rename and Delete; Delete hides the chat from the phone's lists
// as VS Code's Archive does, its history kept, and an open chat returns with its next reply or ask

// the actions' labels
const RENAME = 'Rename'
const DELETE = 'Delete'
const ACTIONS_WIDTH = 148
let swiping = null // the row under a finger: { row, x, y, base, at, isSide }
let swipedOpen = null // the row showing its actions
let isSwipeTap = false // the click that ends a swipe, or closes an open row, opens nothing

function placeSwipe(row, x, isSettling) {
  row.classList.toggle('settle', isSettling)
  row.style.transform = x ? `translateX(${x}px)` : ''
}

function closeSwipe() {
  if (swipedOpen) placeSwipe(swipedOpen, 0, true)
  swipedOpen = null
}

// a row with its actions behind it; VoiceOver reaches them after the row, each naming its chat
function swipeRow(row, session, title) {
  const box = el('div', 'swipe')
  const actions = el('div', 'swipe-actions')
  const rename = button(RENAME, 'act', () => openRenamer(session, title))
  const remove = button(DELETE, 'act danger', () => hideChat(session))
  rename.setAttribute('aria-label', `${RENAME} ${title}`)
  remove.setAttribute('aria-label', `${DELETE} ${title}`)
  actions.append(rename, remove)
  row.classList.add('swipe-row')
  box.append(row, actions)
  return box
}

// a row's own tap opens its chat, unless it ends a swipe or closes the row showing its actions
function isSwipeClick() {
  if (isSwipeTap) {
    isSwipeTap = false
    return true
  }
  if (!swipedOpen) return false
  closeSwipe()
  return true
}

document.addEventListener('pointerdown', event => {
  isSwipeTap = false
  const row = event.target.closest('.swipe-row')
  const isOwnActions = swipedOpen && event.target.closest('.swipe') === swipedOpen.parentElement
  if (swipedOpen && row !== swipedOpen && !isOwnActions) {
    closeSwipe()
    isSwipeTap = true
  }
  if (!row) return
  const base = row === swipedOpen ? -ACTIONS_WIDTH : 0
  swiping = { row, x: event.clientX, y: event.clientY, base, at: base, isSide: null }
})
document.addEventListener('pointermove', event => {
  if (!swiping) return
  const dx = event.clientX - swiping.x
  const dy = event.clientY - swiping.y
  if (swiping.isSide === null) {
    if (Math.abs(dx) > 8 && Math.abs(dx) > Math.abs(dy)) swiping.isSide = true
    else if (Math.abs(dy) > 8) swiping.isSide = false
  }
  if (!swiping.isSide) return
  swiping.at = Math.max(-ACTIONS_WIDTH, Math.min(0, swiping.base + dx))
  placeSwipe(swiping.row, swiping.at, false)
})
function endSwipe() {
  if (!swiping) return
  const { row, at, isSide } = swiping
  swiping = null
  if (!isSide) return
  isSwipeTap = true
  const isOpen = at < -ACTIONS_WIDTH / 2
  placeSwipe(row, isOpen ? -ACTIONS_WIDTH : 0, true)
  swipedOpen = isOpen ? row : swipedOpen === row ? null : swipedOpen
}
document.addEventListener('pointerup', endSwipe)
document.addEventListener('pointercancel', endSwipe)

// the lists drawn again at once with what the phone already knows, ahead of the Mac's next answer
function redrawLists() {
  byId('chats').dataset.key = ''
  byId('recent').dataset.key = ''
  drawChats()
  drawRecent()
}

async function hideChat(session) {
  closeSwipe()
  if (state) {
    state.chats = (state.chats ?? []).filter(c => c.session !== session)
    state.recent = (state.recent ?? []).filter(c => c.session !== session)
  }
  redrawLists()
  try {
    await call('/api/hide', { session })
  } catch {
    bar('offline')
  }
  refresh()
}

let renaming = '' // the chat the rename sheet names

function openRenamer(session, title) {
  closeSwipe()
  renaming = session
  const input = byId('renameText')
  input.value = title
  byId('renamer').hidden = false
  input.focus()
  input.select()
}

function closeRenamer() {
  byId('renamer').hidden = true
  renaming = ''
}

async function saveName() {
  const title = byId('renameText').value.replace(/\s+/g, ' ').trim()
  const session = renaming
  if (!title || !session) return
  closeRenamer()
  for (const c of [...(state?.chats ?? []), ...(state?.recent ?? [])]) if (c.session === session) c.title = title
  redrawLists()
  try {
    await call('/api/rename', { session, title })
  } catch {
    bar('offline')
  }
  refresh()
}

// the lists redraw only when what they show changes; a poll with the same chats leaves them be
function drawChats() {
  // a row swiped open, or under a finger, keeps its place until it closes
  if (swipedOpen || swiping?.isSide) return
  const box = byId('chats')
  const chats = [...(state?.chats ?? [])].sort(order).map(c => ({
    session: c.session,
    state: c.ask ? 'wait' : c.status === 'working' ? 'working' : '',
    // the chat's name as VS Code shows it, its latest reply beneath (its opening words before the first one)
    title: c.title || c.project || 'Chat',
    sub: c.reply?.text || c.began || '',
    ask: c.ask ? (c.ask.kind === 'question' ? 'Question' : 'Permission') : '',
    side: c.status !== 'working' && c.reply ? short(state.now - c.reply.at) : '',
    // the model and effort the chat runs on, as its pill names them; the model alone until its first request
    meta: [modelName(c.model ?? ''), EFFORTS.find(e => e.level === c.effort)?.name].filter(Boolean).join(' · '),
  }))
  const key = JSON.stringify(chats)
  if (box.dataset.key === key) return
  box.dataset.key = key
  box.replaceChildren()
  if (chats.length === 0) {
    box.append(el('div', 'empty', 'Chats show here while open in VS Code'))
    return
  }
  for (const c of chats) {
    const row = button('', 'row', () => {
      if (!isSwipeClick()) location.hash = c.session
    })
    const dot = el('span', `dot${c.state ? ` ${c.state}` : ''}`)
    if (c.state === 'working') inPhase(dot)
    const grow = el('div', 'grow')
    grow.append(el('div', 'title', c.title), el('div', 'sub', c.sub))
    // the pulsing dot's meaning, for VoiceOver
    if (c.state === 'working') grow.append(el('span', 'vh', 'Working'))
    // the time or the ask on top, the model and effort beneath
    const side = el('div', 'side-stack')
    side.append(c.ask ? el('span', 'side attention', c.ask) : el('span', 'side', c.side))
    if (c.meta) side.append(el('span', 'meta', c.meta))
    row.append(dot, grow, side)
    box.append(swipeRow(row, c.session, c.title))
  }
}

// the chats of the last two weeks that are closed: readable, and a message opens one again on the Mac
function drawRecent() {
  if (swipedOpen || swiping?.isSide) return
  const box = byId('recent')
  const recent = (state?.recent ?? []).map(c => ({
    session: c.session,
    title: c.title || c.project || 'Chat',
    side: short(state.now - c.at),
  }))
  byId('recentGroup').hidden = recent.length === 0
  const key = JSON.stringify(recent)
  if (box.dataset.key === key) return
  box.dataset.key = key
  box.replaceChildren()
  for (const c of recent) {
    const row = button('', 'row', () => {
      if (!isSwipeClick()) location.hash = c.session
    })
    const grow = el('div', 'grow')
    grow.append(el('div', 'title', c.title))
    row.append(el('span', 'dot'), grow, el('span', 'side', c.side))
    box.append(swipeRow(row, c.session, c.title))
  }
}

// MARK: the Notifications screen

// a mark per kind of notice
const MARKS = { replies: 'reply', questions: 'question', permissions: 'permission', limits: 'limit' }

const dayStart = ms => new Date(ms).setHours(0, 0, 0, 0)

// Today, Yesterday, a weekday within the week, a date before it
function dayName(start, today) {
  const days = Math.round((today - start) / 86400000)
  if (days === 0) return 'Today'
  if (days === 1) return 'Yesterday'
  const date = new Date(start)
  if (days < 7) return date.toLocaleDateString('en-GB', { weekday: 'long' })
  return date.toLocaleDateString('en-GB', { weekday: 'short', day: 'numeric', month: 'short' })
}

// the log; a notice keeps its dot until opened
async function loadNotices() {
  const since = notices ? `?since=${notices.at}` : ''
  const got = await call(`/api/notices${since}`)
  if (got.same && notices) notices.now = got.now
  else notices = got
  drawNotices()
}

// a card per day, newest first, Clear beside each day's name
function drawNotices() {
  const box = byId('noticeDays')
  const list = notices?.notices ?? []
  const now = notices?.now ?? Date.now()
  const key = JSON.stringify([notices?.at, list.length, Math.floor(now / 60000)])
  if (key === noticesKey) return
  noticesKey = key
  box.replaceChildren()
  if (list.length === 0) {
    const group = el('section', 'group')
    const card = el('div', 'card')
    card.append(el('div', 'empty', 'Replies, questions, permissions and limits gather here'))
    group.append(card)
    box.append(group)
    return
  }
  const today = dayStart(now)
  const days = new Map()
  for (const n of list) {
    const start = dayStart(n.at)
    if (!days.has(start)) days.set(start, [])
    days.get(start).push(n)
  }
  for (const [start, items] of days) {
    const group = el('section', 'group')
    const head = el('h2', 'clearable')
    head.append(el('span', '', dayName(start, today)), button('Clear', 'clear', () => clearDay(items.map(n => n.id))))
    const card = el('div', 'card')
    for (const n of items) card.append(noticeRow(n, start === today, now))
    group.append(head, card)
    box.append(group)
  }
}

// a notice: its kind's mark, its title and age, its words beneath; a tap opens it, and that reads it: its chat, or the
// home for a limit's
function noticeRow(n, isToday, now) {
  const row = button('', `row notice${n.read ? '' : ' unread'}`, () => {
    if (!n.read) call('/api/notices', { read: [n.id] }).catch(() => {})
    location.hash = n.session ?? ''
  })
  const icon = el('span', 'notice-icon')
  icon.append(el('span', `mask ${MARKS[n.kind] ?? 'reply'}-mask`))
  const when = isToday ? short(now - n.at) : new Date(n.at).toLocaleDateString('en-GB', { weekday: 'short' })
  const top = el('div', 'top')
  top.append(el('span', 'title', n.title), el('span', 'side', when))
  const grow = el('div', 'grow')
  grow.append(top, el('div', 'body', n.body ?? ''))
  row.append(icon, grow)
  // the blue dot's meaning, for VoiceOver
  if (!n.read) row.prepend(el('span', 'vh', 'Unread'))
  return row
}

async function clearDay(ids) {
  try {
    await call('/api/notices', { clear: ids })
    const gone = new Set(ids)
    notices = { ...notices, notices: notices.notices.filter(n => !gone.has(n.id)) }
    drawNotices()
  } catch {
    bar('offline')
  }
}

// MARK: the gear: what reaches Notification Center

// the kinds of notice the phone takes, each a switch kept on the Mac
const KINDS = [
  ['replies', 'Replies', 'A chat answered'],
  ['questions', 'Questions', 'A chat asks you to choose'],
  ['permissions', 'Permissions', 'A chat waits for your allow'],
  ['limits', 'Limits', 'At 60, 80 and 100%, and when one resets'],
]

function drawKinds() {
  const box = byId('kinds')
  const notify = state?.notify ?? {}
  const key = JSON.stringify(notify)
  if (box.dataset.key === key) return
  box.dataset.key = key
  box.replaceChildren()
  for (const [kind, title, about] of KINDS) {
    const row = el('label', 'row')
    const grow = el('div', 'grow')
    grow.append(el('div', 'title', title), el('div', 'sub', about))
    const input = el('input', 'switch')
    input.type = 'checkbox'
    input.setAttribute('role', 'switch')
    input.checked = notify[kind] === true
    input.addEventListener('change', async () => {
      try {
        const saved = await call('/api/settings', { notify: { [kind]: input.checked } })
        if (state) state.notify = saved.notify
        box.dataset.key = JSON.stringify(saved.notify)
      } catch {
        input.checked = !input.checked
        bar('offline')
      }
    })
    row.append(grow, input)
    box.append(row)
  }
}

const canPush = () => 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window

async function phonePush() {
  if (!canPush()) return 'unavailable'
  if (Notification.permission === 'denied') return 'blocked'
  const registration = await navigator.serviceWorker.getRegistration()
  const subscription = registration ? await registration.pushManager.getSubscription() : null
  return Notification.permission === 'granted' && subscription ? 'on' : 'off'
}

// the main switch: on while this phone takes pushes and the Mac sends them; off hides the kinds and the test. A phone
// yet to subscribe reads off, and turning it on asks iOS first.
async function drawSettings() {
  if (isSwitching) return
  const found = await phonePush()
  if (isSwitching) return
  phone = found
  const input = byId('mainSwitch')
  const isOn = phone === 'on' && state?.notify?.main !== false
  input.checked = isOn
  input.disabled = phone === 'unavailable' || phone === 'blocked'
  const lines = {
    unavailable: 'Available in the Home Screen app',
    blocked: 'Blocked in iOS Settings',
    off: 'Turn on for this phone',
    on: 'Off hides everything below',
  }
  byId('mainState').textContent = pushNote || lines[phone]
  byId('kindsGroup').hidden = !isOn
  byId('testGroup').hidden = !isOn
  drawKinds()
}

function keyBytes(text) {
  const plain = text.replace(/-/g, '+').replace(/_/g, '/')
  const raw = atob(plain + '='.repeat((4 - (plain.length % 4)) % 4))
  return Uint8Array.from(raw, c => c.charCodeAt(0))
}

// the permission prompt first, inside the tap, as iOS asks; true once the Mac holds this phone's subscription
async function turnOnPush() {
  const permission = await Notification.requestPermission()
  if (permission !== 'granted') return false
  const registration = await navigator.serviceWorker.register('/sw.js')
  await navigator.serviceWorker.ready
  if (!state) state = await call('/api/state')
  const old = await registration.pushManager.getSubscription()
  if (old) await old.unsubscribe()
  const subscription = await registration.pushManager.subscribe({
    userVisibleOnly: true,
    applicationServerKey: keyBytes(state.push.key),
  })
  await call('/api/push', { subscription: subscription.toJSON() })
  return true
}

byId('mainSwitch').addEventListener('change', async () => {
  const input = byId('mainSwitch')
  const wantsOn = input.checked
  isSwitching = true
  pushNote = ''
  try {
    if (wantsOn && phone !== 'on' && !(await turnOnPush())) return
    const saved = await call('/api/settings', { notify: { main: wantsOn } })
    if (state) state.notify = saved.notify
  } catch (error) {
    pushNote = wantsOn ? `Turning on failed: ${error.message}` : ''
    bar('offline')
  } finally {
    isSwitching = false
    drawSettings()
  }
})

byId('pushTest').addEventListener('click', async () => {
  const words = byId('testState')
  try {
    await call('/api/test', {})
    words.textContent = 'Sent to this phone'
  } catch (error) {
    words.textContent = `Test failed: ${error.message}`
  }
  setTimeout(() => {
    words.textContent = 'One notice to this phone now'
  }, 4000)
})

// MARK: replies as safe HTML: every character escaped first, then a fixed set of tags

const escapeHTML = text =>
  text.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c])

// code in colour by highlight.js, each language read by its own grammar; its
// spans take VS Code's colours in app.css. VS Code shows flow words (if, return, import) in a colour of their own, so
// those keywords carry a mark for it. A language highlight.js lacks stays plain.
const CONTROL = new Set(
  ('if else elif for while do return switch case default break continue throw throws try catch finally await yield ' +
    'import export from as in of guard defer repeat except raise with pass then fi done esac match loop when').split(' '),
)

// highlighted HTML cut into lines: the spans open at a line's end close there and open again on the next line, so a
// comment or a string across lines keeps its colour on each
function splitLines(html) {
  const lines = []
  const open = []
  let line = ''
  for (const [part] of html.matchAll(/<span[^>]*>|<\/span>|[^<]+/g)) {
    if (part.startsWith('<span')) {
      open.push(part)
      line += part
    } else if (part === '</span>') {
      open.pop()
      line += part
    } else {
      part.split('\n').forEach((text, index) => {
        if (index > 0) {
          lines.push(line + '</span>'.repeat(open.length))
          line = open.join('')
        }
        line += text
      })
    }
  }
  lines.push(line)
  return lines
}

// the code's lines in colour, as HTML
function highlightLines(text, language) {
  if (!language || !hljs.getLanguage(language)) return text.split('\n').map(escapeHTML)
  const html = hljs
    .highlight(text, { language, ignoreIllegals: true })
    .value.replace(/<span class="hljs-keyword">(\w+)<\/span>/g, (whole, word) =>
      CONTROL.has(word) ? `<span class="hljs-keyword hljs-control">${word}</span>` : whole,
    )
  return splitLines(html)
}

// the language a file's name says, by highlight.js's names
const EXTENSIONS = {
  js: 'javascript', mjs: 'javascript', cjs: 'javascript', jsx: 'javascript', ts: 'typescript', tsx: 'typescript',
  mts: 'typescript', swift: 'swift', py: 'python', sh: 'bash', zsh: 'bash', bash: 'bash', css: 'css', scss: 'scss',
  less: 'less', html: 'xml', htm: 'xml', xml: 'xml', plist: 'xml', svg: 'xml', json: 'json', jsonl: 'json',
  md: 'markdown', yml: 'yaml', yaml: 'yaml', toml: 'ini', ini: 'ini', conf: 'ini', env: 'bash', rb: 'ruby', go: 'go',
  rs: 'rust', php: 'php', sql: 'sql', java: 'java', kt: 'kotlin', c: 'c', h: 'c', cpp: 'cpp', m: 'objectivec',
  lua: 'lua', graphql: 'graphql', diff: 'diff', makefile: 'makefile', txt: 'plaintext',
}
const languageOf = path => {
  const name = (path ?? '').split('/').pop().toLowerCase()
  return EXTENSIONS[name.includes('.') ? name.split('.').pop() : name] ?? 'plaintext'
}

// lines as an editor shows them: a gutter on the left (the line's number, and + or − in an edit) and the code beside
// it; a long line wraps under its own code, so every line starts at the same edge. Each row: { no, mark, kind, html },
// kind '' or added or removed.
function editorLines(rows, className = '') {
  const cells = rows.map(r => {
    const kind = r.kind ? ` ${r.kind}` : ''
    return `<span class="no${kind}">${r.no ?? ''}</span><span class="mk${kind}">${r.mark ?? ''}</span>` +
      `<span class="tx${kind}">${r.html ?? ''}</span>`
  })
  return `<div class="lines${className ? ` ${className}` : ''}">${cells.join('')}</div>`
}

// a block's lines, one trailing break left out the way an editor ends a file
const linesOf = text => {
  const lines = text.split('\n')
  if (lines.length > 1 && lines.at(-1) === '') lines.pop()
  return lines
}

// a code block as an editor shows code: the fence's language and a copy button above, the lines numbered in a
// gutter, the code in colour; the copy takes the code alone, kept whole on the block
function codeBlock(text, language) {
  const head =
    `<div class="code-head"><span class="lang">${escapeHTML(language)}</span>` +
    '<button class="copy" type="button" aria-label="Copy"><span class="mask copy-mask"></span></button></div>'
  const shown = highlightLines(linesOf(text).join('\n'), language)
  const rows = shown.map((html, index) => ({ no: index + 1, html }))
  return `<div class="code" data-raw="${escapeHTML(text.replace(/\n$/, ''))}">${head}${editorLines(rows)}</div>`
}

// the clipboard, with the older Safari way behind it: a selected box off screen and the copy command
async function copyText(text) {
  if (navigator.clipboard?.writeText) return navigator.clipboard.writeText(text)
  const box = document.createElement('textarea')
  box.value = text
  box.setAttribute('readonly', '')
  box.style.position = 'fixed'
  box.style.opacity = '0'
  document.body.append(box)
  box.select()
  const isCopied = document.execCommand('copy')
  box.remove()
  if (!isCopied) throw new Error('Copy refused')
}

function inline(raw) {
  return raw
    .split(/(`[^`\n]+`)/g)
    .map((part, i) => {
      if (i % 2 === 1) return `<code>${escapeHTML(part.slice(1, -1))}</code>`
      return escapeHTML(part)
        .replace(/\*\*([^*]+?)\*\*/g, '<strong>$1</strong>')
        .replace(/(^|[\s(])\*([^*\s][^*]*?)\*(?=[\s).,;:!?]|$)/g, '$1<em>$2</em>')
        .replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g, '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>')
        .replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, '$1')
    })
    .join('')
}

// a run of list items; each two spaces of indent go one level deeper
function list(items) {
  let html = ''
  const open = []
  for (const item of items) {
    const depth = Math.min(item.depth, open.length)
    while (open.length > depth + 1) html += `</li></${open.pop()}>`
    if (open.length === depth + 1) {
      html += '</li>'
    } else {
      const tag = item.isOrdered ? 'ol' : 'ul'
      html += `<${tag}>`
      open.push(tag)
    }
    html += `<li>${inline(item.text)}`
  }
  while (open.length > 0) html += `</li></${open.pop()}>`
  return html
}

function markdown(source) {
  const lines = String(source).replace(/\r\n?/g, '\n').split('\n')
  const isFence = line => /^\s*```/.test(line)
  const isItem = line => /^\s*(?:[-*+]|\d+[.)])\s+/.test(line)
  const isTableRow = line => /^\s*\|.*\|\s*$/.test(line)
  const isTableRule = line => /^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$/.test(line)
  const isRule = line => /^\s*([-*_])\s*\1\s*\1[\s\-*_]*$/.test(line)
  const startsBlock = line =>
    isFence(line) || /^#{1,6}\s/.test(line) || isItem(line) || /^>\s?/.test(line) || isTableRow(line) || isRule(line)
  const cells = row =>
    row
      .trim()
      .replace(/^\|/, '')
      .replace(/\|$/, '')
      .split('|')
      .map(cell => inline(cell.trim()))
  const out = []
  let i = 0
  while (i < lines.length) {
    const line = lines[i]
    if (isFence(line)) {
      const language = line.trim().slice(3).trim().split(/\s+/)[0].toLowerCase().slice(0, 20)
      const body = []
      i += 1
      while (i < lines.length && !isFence(lines[i])) {
        body.push(lines[i])
        i += 1
      }
      i += 1
      out.push(codeBlock(body.join('\n'), language))
      continue
    }
    if (!line.trim()) {
      i += 1
      continue
    }
    const heading = line.match(/^(#{1,6})\s+(.*)$/)
    if (heading) {
      const level = heading[1].length <= 2 ? 3 : 4
      out.push(`<h${level}>${inline(heading[2])}</h${level}>`)
      i += 1
      continue
    }
    if (isRule(line)) {
      out.push('<hr>')
      i += 1
      continue
    }
    if (isTableRow(line) && i + 1 < lines.length && isTableRule(lines[i + 1])) {
      const head = cells(line)
      const body = []
      i += 2
      while (i < lines.length && isTableRow(lines[i])) {
        body.push(cells(lines[i]))
        i += 1
      }
      const headHTML = head.map(c => `<th>${c}</th>`).join('')
      const bodyHTML = body.map(r => `<tr>${r.map(c => `<td>${c}</td>`).join('')}</tr>`).join('')
      out.push(`<div class="table"><table><thead><tr>${headHTML}</tr></thead><tbody>${bodyHTML}</tbody></table></div>`)
      continue
    }
    if (isItem(line)) {
      const items = []
      while (i < lines.length && (isItem(lines[i]) || (items.length > 0 && /^\s{2,}\S/.test(lines[i])))) {
        const match = lines[i].match(/^(\s*)(?:([-*+])|(\d+)[.)])\s+(.*)$/)
        if (match) {
          const indent = match[1].replace(/\t/g, '  ').length
          items.push({ depth: Math.floor(indent / 2), isOrdered: match[3] !== undefined, text: match[4] })
        } else {
          items[items.length - 1].text += ` ${lines[i].trim()}`
        }
        i += 1
      }
      out.push(list(items))
      continue
    }
    if (/^>\s?/.test(line)) {
      const body = []
      while (i < lines.length && /^>\s?/.test(lines[i])) {
        body.push(lines[i].replace(/^>\s?/, ''))
        i += 1
      }
      out.push(`<blockquote>${markdown(body.join('\n'))}</blockquote>`)
      continue
    }
    const paragraph = []
    while (i < lines.length && lines[i].trim() && !startsBlock(lines[i])) {
      paragraph.push(lines[i].trim())
      i += 1
    }
    if (paragraph.length === 0) {
      paragraph.push(line.trim())
      i += 1
    }
    out.push(`<p>${paragraph.map(inline).join('<br>')}</p>`)
  }
  return out.join('')
}

// MARK: the chat

// a message from the phone names its files by path beneath the words; the bubble shows each by its name
function splitFiles(text) {
  const at = text.lastIndexOf(ATTACHED)
  if (at === -1) return { words: text, names: [] }
  const paths = text.slice(at + ATTACHED.length).trim().split(/\s+(?=\/)/).filter(Boolean)
  return { words: text.slice(0, at).trim(), names: paths.map(path => path.split('/').pop()) }
}

// a slash command as the panel shows one: the command on the sender's side, its output beneath in grey
function commandRow(m) {
  const node = el('div', 'command')
  node.append(el('div', 'msg user command-line', m.text))
  if (m.output) node.append(el('div', 'command-output', m.output))
  return node
}

// a message: the answer as markdown; the person's words with their pictures (pasted at the Mac, or sent from here,
// shown once the Mac holds them) and a line per other file
function message(m, note) {
  if (m.role === 'tool') return toolRow(m)
  if (m.role === 'summary') return summaryRow(m)
  if (m.role === 'command') return commandRow(m)
  const node = el('div', `msg ${m.role}${note === 'Queued' ? ' queued' : ''}`)
  if (m.role === 'assistant') {
    node.innerHTML = markdown(m.text)
  } else {
    const { words, names } = m.names ? { words: m.text, names: m.names } : splitFiles(m.text)
    if (words) node.append(el('div', 'words', words))
    const shots = (m.images ?? []).map(key => thumb(imageKey(key)))
    for (const name of names) {
      if (m.isSaved !== false && isPictureName(name)) {
        shots.push(thumb(imageFile(name)))
      } else {
        const row = el('div', 'file')
        row.append(el('span', 'mask file-mask'), el('span', 'name', name))
        node.append(row)
      }
    }
    if (shots.length > 0) {
      const strip = el('div', 'shots')
      strip.append(...shots)
      node.append(strip)
    }
  }
  if (note) node.append(el('span', 'state', note))
  return node
}

// an MCP tool reads as its server and its name
const toolName = name => (name.startsWith('mcp__') ? name.slice(5).replace('__', ' ') : name)

// where a compaction summed up the chat before it: one line, the summary itself on a tap
function summaryRow(m) {
  const id = `summary-${m.text.length}`
  const node = el('div', `summary${opened.has(id) ? ' open' : ''}`)
  const line = button('Conversation summarized here', 'line', () => {
    if (opened.has(id)) opened.delete(id)
    else opened.add(id)
    node.classList.toggle('open', opened.has(id))
    line.setAttribute('aria-expanded', String(opened.has(id)))
  })
  line.setAttribute('aria-expanded', String(opened.has(id)))
  const body = el('div', 'details msg assistant')
  body.innerHTML = markdown(m.text)
  node.append(line, body)
  return node
}

// the top of the chat: a page from before what shows, on a tap
function earlierRow() {
  const label = earlier.isLoading ? 'Loading' : 'Earlier messages'
  const node = button(label, 'earlier', () => loadEarlier())
  node.disabled = earlier.isLoading
  return node
}

// a page from the transcript, put above what shows with the view held where it was
async function loadEarlier() {
  if (earlier.isLoading || earlier.cursor === null || !chat) return
  const target = session
  earlier.isLoading = true
  drawChat()
  let query = ''
  if (typeof earlier.cursor === 'number') {
    query = `&before=${earlier.cursor}`
  } else {
    const first = (chat.history ?? [])[0]
    if (first?.role === 'tool') query = `&tool=${encodeURIComponent(first.id)}`
    else if (first?.text) query = `&text=${encodeURIComponent(first.text.slice(0, 100))}`
  }
  try {
    const page = await call(`/api/earlier?session=${encodeURIComponent(target)}${query}`)
    if (session !== target) return
    const box = byId('messages')
    const fromEnd = box.scrollHeight - box.scrollTop
    earlier = { items: [...page.items, ...earlier.items], cursor: page.cursor, isLoading: false }
    drawChat()
    box.scrollTop = box.scrollHeight - fromEnd
  } catch {
    earlier.isLoading = false
    if (session === target) drawChat()
    bar('offline')
  }
}

// MARK: tool calls as VS Code shows code

// two texts' lines matched up the way a diff view shows them: shared lines once, removed lines before the added ones
// that replace them; each row { kind, a, b } with a and b the line's index in the old and the new text
function diffLines(a, b) {
  let start = 0
  while (start < a.length && start < b.length && a[start] === b[start]) start += 1
  let endA = a.length
  let endB = b.length
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) {
    endA -= 1
    endB -= 1
  }
  const rows = []
  for (let i = 0; i < start; i += 1) rows.push({ kind: '', a: i, b: i })
  const n = endA - start
  const m = endB - start
  if (n > 0 && m > 0 && n * m <= 250_000) {
    // the longest run of shared lines, by the usual table
    const table = Array.from({ length: n + 1 }, () => new Uint16Array(m + 1))
    for (let i = n - 1; i >= 0; i -= 1) {
      for (let j = m - 1; j >= 0; j -= 1) {
        table[i][j] =
          a[start + i] === b[start + j] ? table[i + 1][j + 1] + 1 : Math.max(table[i + 1][j], table[i][j + 1])
      }
    }
    let i = 0
    let j = 0
    while (i < n && j < m) {
      if (a[start + i] === b[start + j]) {
        rows.push({ kind: '', a: start + i, b: start + j })
        i += 1
        j += 1
      } else if (table[i + 1][j] >= table[i][j + 1]) {
        rows.push({ kind: 'removed', a: start + i })
        i += 1
      } else {
        rows.push({ kind: 'added', b: start + j })
        j += 1
      }
    }
    for (; i < n; i += 1) rows.push({ kind: 'removed', a: start + i })
    for (; j < m; j += 1) rows.push({ kind: 'added', b: start + j })
  } else {
    for (let i = start; i < endA; i += 1) rows.push({ kind: 'removed', a: i })
    for (let j = start; j < endB; j += 1) rows.push({ kind: 'added', b: j })
  }
  for (let k = 0; k < a.length - endA; k += 1) rows.push({ kind: '', a: endA + k, b: endB + k })
  return rows
}

// the most lines a view draws; the rest is counted beneath
const VIEW_LINES = 1500

function capped(rows) {
  if (rows.length <= VIEW_LINES) return rows
  const more = rows.length - VIEW_LINES
  return [...rows.slice(0, VIEW_LINES), { html: `<span class="more">${more} more lines</span>` }]
}

// the file's name with its folder dimmed beside it, as an editor tab; numbers to the right
function fileHead(path, side = '') {
  const parts = (path ?? '').split('/')
  const name = parts.pop() || path || ''
  const folder = parts.slice(-3).join('/')
  return `<div class="editor-head"><span class="file">${escapeHTML(name)}</span>` +
    `<span class="path">${escapeHTML(folder)}</span>${side}</div>`
}

// an edit as a diff: the file's own line numbers where the Mac found them, − and + in the gutter, red and green rows
function editView(input, line) {
  const language = languageOf(input.file_path)
  const before = linesOf(input.old_string ?? '')
  const after = linesOf(input.new_string ?? '')
  const oldHTML = highlightLines(before.join('\n'), language)
  const newHTML = highlightLines(after.join('\n'), language)
  const diff = diffLines(before, after)
  const number = index => (line ? line + index : '')
  const rows = diff.map(d =>
    d.kind === 'removed'
      ? { kind: 'removed', no: number(d.a), mark: '−', html: oldHTML[d.a] }
      : d.kind === 'added'
        ? { kind: 'added', no: number(d.b), mark: '+', html: newHTML[d.b] }
        : { no: number(d.b), mark: ' ', html: newHTML[d.b] },
  )
  const added = diff.filter(d => d.kind === 'added').length
  const removed = diff.filter(d => d.kind === 'removed').length
  const counts = `<span class="counts"><span class="plus">+${added}</span> <span class="minus">−${removed}</span></span>`
  return `<div class="editor">${fileHead(input.file_path, counts)}${editorLines(capped(rows), 'diff')}</div>`
}

// a file's lines with their numbers: a Read's result as the tool numbers it, or a written file from its first line
function fileView(path, numbered) {
  const language = languageOf(path)
  const html = highlightLines(numbered.map(l => l.text).join('\n'), language)
  const rows = numbered.map((l, index) => ({ no: l.no, html: html[index] }))
  const first = numbered[0]?.no
  const last = numbered.at(-1)?.no
  const range = first && last && last > first ? `<span class="counts">lines ${first}–${last}</span>` : ''
  return `<div class="editor">${fileHead(path, range)}${editorLines(capped(rows))}</div>`
}

// a Read's result: each line its number and its text; the reminders Claude Code appends stay out
function readLines(output) {
  const lines = []
  for (const raw of (output ?? '').split('\n')) {
    if (raw.startsWith('<system-reminder>')) break
    const found = /^\s*(\d+)(?:\t|→)(.*)$/.exec(raw)
    if (found) lines.push({ no: Number(found[1]), text: found[2] })
  }
  return lines
}

// plain lines, wrapped under themselves: a command's output, a search's hits
function plainView(text, className = '') {
  const rows = linesOf(text).map(line => ({ html: escapeHTML(line) }))
  return `<div class="editor${className ? ` ${className}` : ''}">${editorLines(capped(rows), 'plain')}</div>`
}

// a command as a terminal shows it: its lines in the shell's colours, a $ before the first
function commandView(command) {
  const rows = highlightLines(linesOf(command).join('\n'), 'sh').map((html, index) => ({
    mark: index === 0 ? '$' : '',
    html,
  }))
  return `<div class="editor">${editorLines(capped(rows), 'command')}</div>`
}

// the fields a tool's input reads best as text, in order
const TEXT_FIELDS = {
  Grep: ['pattern', 'path', 'glob'],
  Glob: ['pattern', 'path'],
  WebFetch: ['url', 'prompt'],
  WebSearch: ['query'],
  Agent: ['description', 'prompt'],
  Task: ['description', 'prompt'],
}

// what a tool call shows once opened: the whole call from the transcript when the Mac has sent it, else what the
// history row holds, read back into the same fields
function callView(m, whole) {
  let input = whole?.input
  let output = whole ? whole.output : m.output
  const isError = whole ? whole.isError === true : m.state === 'error'
  if (!input) {
    const detail = m.detail ?? ''
    const [path, ...rest] = detail.split('\n')
    const body = rest.join('\n').replace(/^\n/, '')
    if (m.tool === 'Edit') {
      const marked = linesOf(body)
      input = {
        file_path: path,
        old_string: marked.filter(l => l.startsWith('- ') || l === '-').map(l => l.slice(2)).join('\n'),
        new_string: marked.filter(l => l.startsWith('+ ') || l === '+').map(l => l.slice(2)).join('\n'),
      }
    } else if (m.tool === 'Write') input = { file_path: path, content: body }
    else if (m.tool === 'Read') input = { file_path: path }
    else if (m.tool === 'Bash') input = { command: detail }
    else input = { text: detail }
  }
  const parts = []
  if (m.tool === 'Edit') parts.push(editView(input, whole?.line))
  else if (m.tool === 'Write') {
    parts.push(fileView(input.file_path, linesOf(input.content ?? '').map((text, index) => ({ no: index + 1, text }))))
  } else if (m.tool === 'Read') {
    const lines = readLines(output)
    if (lines.length > 0) {
      parts.push(fileView(input.file_path, lines))
      output = ''
    } else parts.push(`<div class="editor">${fileHead(input.file_path)}</div>`)
  } else if (m.tool === 'Bash') parts.push(commandView(input.command ?? ''))
  else {
    // a tool's own words as text (a search's pattern, an agent's prompt), any other input as JSON
    const fields = TEXT_FIELDS[m.tool]
    const words = input.text ?? fields?.map(key => input[key]).filter(v => typeof v === 'string' && v).join('\n\n')
    const text = words || JSON.stringify(input, null, 2)
    const rows = highlightLines(linesOf(text).join('\n'), words ? 'text' : 'json').map(html => ({ html }))
    if (text && text !== '{}') parts.push(`<div class="editor">${editorLines(capped(rows), 'plain')}</div>`)
  }
  // a successful edit's line adds nothing beside its diff
  const isEditNote = (m.tool === 'Edit' || m.tool === 'Write') && !isError
  if (output?.trim() && !isEditNote) parts.push(plainView(output.replace(/\n?<system-reminder>[\s\S]*$/, ''), `output${isError ? ' error' : ''}`))
  return parts.join('')
}

// the calls fetched whole, by id, with the state each was fetched in: a running call's result comes once it ends
const wholeCalls = new Map()

async function loadCall(m, redraw) {
  const held = wholeCalls.get(m.id)
  if (held && held.state === m.state) return
  wholeCalls.set(m.id, { state: m.state, isLoading: true })
  try {
    const whole = await call(`/api/tool?session=${encodeURIComponent(session)}&id=${encodeURIComponent(m.id)}`)
    wholeCalls.set(m.id, { state: m.state, whole })
    redraw()
  } catch {
    // the history's own row stands: a call in a subagent's thread, or the transcript a moment behind
    wholeCalls.set(m.id, { state: m.state, failed: true })
  }
}

// a tool call as the panel shows it: its state, its name and its target on one line; a tap opens the call in full and
// what it answered; the pictures it handed the model show beneath at once
function toolRow(m) {
  const node = el('div', `tool ${m.state}${opened.has(m.id) ? ' open' : ''}`)
  node.dataset.id = m.id
  const details = el('div', 'details')
  const draw = () => {
    details.innerHTML = callView(m, wholeCalls.get(m.id)?.whole)
  }
  const line = button('', 'line', () => {
    if (opened.has(m.id)) opened.delete(m.id)
    else opened.add(m.id)
    node.classList.toggle('open', opened.has(m.id))
    line.setAttribute('aria-expanded', String(opened.has(m.id)))
    if (opened.has(m.id)) loadCall(m, draw)
  })
  line.setAttribute('aria-expanded', String(opened.has(m.id)))
  const dot = el('span', `dot ${m.state === 'running' ? 'working' : m.state}`)
  if (m.state === 'running') inPhase(dot)
  line.append(dot, el('span', 'name', toolName(m.tool)))
  if (m.target) line.append(el('span', 'target', m.target))
  node.append(line)
  draw()
  if (details.innerHTML) node.append(details)
  if (opened.has(m.id)) loadCall(m, draw)
  if ((m.images ?? []).length > 0) {
    const strip = el('div', 'shots')
    strip.append(...m.images.map(key => thumb(imageKey(key))))
    node.append(strip)
  }
  return node
}

// Working and the tools running, with Stop while a turn runs
function progress(tools, canStop) {
  const row = el('div', 'progress')
  row.append(inPhase(el('span', 'dot working')), el('span', 'what', tools.length > 0 ? `Working · ${tools.join(', ')}` : 'Working'))
  if (canStop) {
    // an icon alone; the words stay for VoiceOver, and Stopping shows as the pill dimmed
    const stop = button('', 'pill stop', stopTurn)
    stop.setAttribute('aria-label', stopAt ? 'Stopping' : 'Stop')
    stop.append(el('span', 'mask stop-mask'))
    stop.disabled = stopAt > 0
    row.append(stop)
  }
  return row
}

async function stopTurn() {
  stopAt = Date.now()
  drawChat()
  try {
    await call('/api/stop', { session })
  } catch {
    stopAt = 0
    drawChat()
    bar('offline')
  }
}

function drawChat() {
  byId('chatProject').textContent = chat.title || chat.project || 'Chat'
  byId('chatBegan').textContent = chat.title ? '' : chat.began || ''
  const outbox = chat.outbox ?? []
  const shown = new Set(outbox.map(m => m.id))
  // a message sent from here gives way once the Mac shows it: in the outbox, or settled into the history
  pending = pending.filter(
    p => p.session !== session || !p.id || !(shown.has(p.id) || (chat.settled && p.id <= chat.settled)),
  )
  const mine = pending.filter(p => p.session === session)
  const isRunning = chat.status === 'working'
  const newest = outbox.at(-1)
  // Working from the moment a message leaves: the chat takes it within two seconds and its turn starts
  const isWorking = isRunning || mine.length > 0 || (newest !== undefined && chat.now - newest.at < 15000)
  if (stopAt && (!isRunning || Date.now() - stopAt > 15000)) stopAt = 0
  const tools = isRunning ? (chat.tools ?? []).map(toolName) : []
  const key = JSON.stringify([
    chat.historyAt,
    outbox,
    mine.map(p => [p.key, p.names, p.isSaved]),
    isWorking,
    isRunning,
    tools,
    stopAt > 0,
    earlier.items.length,
    earlier.cursor,
    earlier.isLoading,
  ])
  if (key !== chatKey) {
    const box = byId('messages')
    const isFirst = chatKey === ''
    const isAtEnd = isScrollDue || isFirst || box.scrollHeight - box.scrollTop - box.clientHeight < 80
    chatKey = key
    isScrollDue = false
    const rows = []
    if (earlier.cursor !== null) rows.push(keyedRow(['earlier', earlier.isLoading], () => earlierRow()))
    for (const m of earlier.items) rows.push(keyedRow(['m', m], () => message(m)))
    for (const m of chat.history ?? []) rows.push(keyedRow(['m', m], () => message(m)))
    const note = isWorking ? '' : 'Queued'
    for (const m of outbox) rows.push(keyedRow(['o', m.id, m.text, note], () => message({ role: 'user', text: m.text }, note)))
    for (const p of mine) {
      const sent = { role: 'user', text: p.text, names: p.names, isSaved: p.isSaved }
      rows.push(keyedRow(['p', p.key, sent], () => message(sent)))
    }
    if (isWorking) rows.push(keyedRow(['progress', tools, isRunning, stopAt > 0], () => progress(tools, isRunning)))
    placeRows(box, rows)
    // new rows glide into view for a reader at the end; one reading higher up stays where they are
    const isStill = isFirst || matchMedia('(prefers-reduced-motion: reduce)').matches
    if (isAtEnd) box.scrollTo({ top: box.scrollHeight, behavior: isStill ? 'auto' : 'smooth' })
  }
  drawAsk(chat.ask)
  drawSwitch()
}

// MARK: the chat's model and effort

// a model's name from its id, as the panel names it: claude-opus-5-5 reads Opus 5.5
function modelName(id = '') {
  const found = /(opus|sonnet|haiku|fable)(?:[-_ ]?(\d+)(?:[-.](\d+))?)?/i.exec(id)
  if (!found) return id
  const family = found[1][0].toUpperCase() + found[1].slice(1).toLowerCase()
  return found[2] ? `${family} ${found[2]}${found[3] ? `.${found[3]}` : ''}` : family
}

const familyOf = id => MODELS.find(m => (id ?? '').toLowerCase().includes(m.alias))?.alias

// the switch asked from here stands until the chat reports one it took at or after it
function isTaken(w) {
  return (chat?.switchedAt ?? 0) >= w.at
}

// the pill above the message box: what the chat runs on, or the switch on its way, dimmed
function drawSwitch() {
  const pill = byId('modelPill')
  const isShown = Boolean(chat?.model) && session !== NEW
  pill.hidden = !isShown
  if (!isShown) return
  if (wanted && (wanted.session !== session || isTaken(wanted))) wanted = null
  const mine = wanted?.session === session ? wanted : null
  const model = mine?.model ? MODELS.find(m => m.alias === mine.model).name : modelName(chat.model)
  const level = EFFORTS.find(e => e.level === (mine?.effort ?? chat.effort))
  byId('modelText').textContent = level ? `${model} · ${level.name}` : model
  pill.classList.toggle('pending', Boolean(mine))
  if (!byId('switcher').hidden) drawSwitcher()
}

// the sheet: the models as a list with a check on the current one, the levels as one segmented row
function drawSwitcher() {
  const mine = wanted?.session === session ? wanted : null
  const current = mine?.model ?? familyOf(chat.model)
  byId('models').replaceChildren(
    ...MODELS.map(m => {
      const row = button('', 'row choice', () => askSwitch({ model: m.alias }))
      row.append(el('div', 'grow', m.name))
      if (m.alias === current) row.append(el('span', 'mask check-mask'))
      row.setAttribute('aria-pressed', String(m.alias === current))
      return row
    }),
  )
  const level = mine?.effort ?? chat.effort
  if (!isSliding) drawLevel(EFFORTS.findIndex(e => e.level === level))
  byId('switchNote').textContent = chat.status === 'working' ? SWITCH_LATER : ''
}

// the effort slider at a level: the track filled up to it, its mark under the thumb, its name in bold; a level the chat
// has yet to report (-1) leaves the thumb dimmed in the middle
let isSliding = false

function drawLevel(index) {
  const input = byId('effort')
  const at = index < 0 ? 2 : index
  input.value = String(at)
  input.classList.toggle('unknown', index < 0)
  input.style.setProperty('--v', String(index < 0 ? 0 : at))
  input.setAttribute('aria-valuetext', index < 0 ? '' : EFFORTS[at].name)
  const placed = (node, i) => {
    node.style.setProperty('--i', String(i))
    return node
  }
  byId('effortTicks').replaceChildren(
    ...EFFORTS.map((e, i) => placed(el('span', i === at ? 'under' : index >= 0 && i < at ? 'filled' : ''), i)),
  )
  byId('effortNames').replaceChildren(...EFFORTS.map((e, i) => placed(el('span', i === index ? 'on' : '', e.name), i)))
}

// a pick goes to the chat at once; the sheet stays open for the other half
async function askSwitch(change) {
  const before = wanted?.session === session ? wanted : null
  wanted = { session, model: before?.model, effort: before?.effort, ...change, at: Date.now() }
  drawSwitch()
  try {
    await call('/api/switch', { session, ...change, at: wanted.at })
  } catch {
    wanted = before
    drawSwitch()
    bar('offline')
  }
}

// the chat's rows between draws, by what each shows: a row that stays the same keeps its node, so its pictures stay
// loaded and the page holds still around it; a changed row takes its place, and new rows join at the end
let rowNodes = new Map()

const keyedRow = (parts, make) => ({ key: JSON.stringify(parts), make })

function placeRows(box, rows) {
  const next = new Map()
  const counts = new Map()
  rows.forEach((each, index) => {
    // the same row twice (a message sent twice) keeps a node each
    const count = counts.get(each.key) ?? 0
    counts.set(each.key, count + 1)
    const key = `${count}:${each.key}`
    const node = rowNodes.get(key) ?? each.make()
    next.set(key, node)
    const current = box.children[index]
    if (current !== node) box.insertBefore(node, current ?? null)
  })
  while (box.children.length > rows.length) box.lastElementChild.remove()
  rowNodes = next
}

function drawAsk(ask) {
  const box = byId('ask')
  if (!ask) {
    box.hidden = true
    askId = ''
    return
  }
  // the same ask keeps its card and the picks in it
  if (ask.id === askId) return
  askId = ask.id
  drafts = {}
  box.hidden = false
  const questions = ask.kind === 'permission' ? [] : (ask.questions ?? [])
  // the card's top row folds it to that row: the chat above takes the room for reading, and one tap opens
  // the card again to answer. Folded, the row carries the ask's first line; a new ask arrives open.
  const isPermission = ask.kind === 'permission'
  const kind = isPermission ? 'Permission' : questions[0]?.header || 'Question'
  const firstLine = isPermission
    ? [ask.tool || 'Tool', (ask.detail ?? '').split('\n')[0]].filter(Boolean).join(' · ')
    : questions[0]?.question ?? ''
  const head = button('', 'ask-head', () => foldAsk(ask.id))
  head.append(el('span', 'kind', kind), el('span', 'ask-summary', firstLine), el('span', 'mask fold-mask'))
  const body = el('div', 'ask-body')
  box.replaceChildren(head, body)
  showFold(folded.has(ask.id))
  if (isPermission) {
    body.append(el('div', 'question', ask.tool || 'Tool'))
    // the call in full, drawn as its tool row draws it once opened: an edit's diff, a written file, a command
    if (ask.detail) {
      const view = el('div', 'ask-call')
      view.innerHTML = callView({ tool: ask.tool, detail: ask.detail })
      body.append(view)
    }
    if (ask.more > 0) body.append(el('div', 'ask-more', `${ask.more.toLocaleString()} more characters show on the Mac`))
    const actions = el('div', 'actions')
    actions.append(
      button('Deny', '', () => answer({ allow: false })),
      button('Allow', 'strong', () => answer({ allow: true })),
    )
    body.append(actions)
    return
  }
  const send = button('Send answer', 'strong', () => {
    const answers = {}
    for (const q of questions) {
      const draft = drafts[q.question]
      answers[q.question] = draft.other || draft.labels.join(', ')
    }
    answer({ answers })
  })
  const ready = () => {
    send.disabled = !questions.every(q => drafts[q.question]?.other || drafts[q.question]?.labels.length)
  }
  questions.forEach((q, index) => {
    // the first question's kind rides in the top row
    if (index > 0) body.append(el('div', 'kind', q.header || 'Question'))
    body.append(el('div', 'question', q.question))
    const options = []
    const other = el('input', 'other')
    other.placeholder = 'Other answer'
    other.setAttribute('aria-label', 'Other answer')
    // a pick shows in blue, and VoiceOver reads it as selected
    const mark = (each, isPicked) => {
      each.classList.toggle('picked', isPicked)
      each.setAttribute('aria-pressed', String(isPicked))
    }
    for (const option of q.options ?? []) {
      const node = button('', 'option', () => {
        const picked = new Set(drafts[q.question]?.labels ?? [])
        if (q.multiSelect) {
          if (picked.has(option.label)) picked.delete(option.label)
          else picked.add(option.label)
        } else {
          picked.clear()
          picked.add(option.label)
        }
        drafts[q.question] = { labels: [...picked], other: '' }
        other.value = ''
        for (const [each, label] of options) mark(each, picked.has(label))
        ready()
      })
      node.append(el('div', 'label', option.label))
      if (option.description) node.append(el('div', 'about', option.description))
      node.setAttribute('aria-pressed', 'false')
      options.push([node, option.label])
      body.append(node)
    }
    other.addEventListener('input', () => {
      const typed = other.value.trim()
      if (typed) {
        drafts[q.question] = { labels: [], other: typed }
        for (const [each] of options) mark(each, false)
      } else {
        delete drafts[q.question]
      }
      ready()
    })
    body.append(other)
  })
  const actions = el('div', 'actions')
  actions.append(send)
  body.append(actions)
  ready()
}

// the asks folded on this phone, by id, kept while the page lives so a folded card stays folded on a return to its chat
const folded = new Set()

function showFold(isFolded) {
  const box = byId('ask')
  box.classList.toggle('folded', isFolded)
  box.querySelector('.ask-head')?.setAttribute('aria-expanded', String(!isFolded))
}

function foldAsk(id) {
  if (folded.has(id)) folded.delete(id)
  else folded.add(id)
  showFold(folded.has(id))
}

async function answer(body) {
  const box = byId('ask')
  const buttons = [...box.querySelectorAll('button')]
  for (const each of buttons) each.disabled = true
  try {
    await call('/api/answer', { session, ask: askId, ...body })
    box.replaceChildren(el('div', 'kind', 'Answer sent'))
  } catch (error) {
    // the ask moved on meanwhile: answered at the Mac, its turn over, or a newer ask in its place
    if (error.message === 'HTTP 409') return box.replaceChildren(el('div', 'kind', 'Settled on the Mac'))
    box.append(el('div', 'sub', `Sending failed: ${error.message}`))
    for (const each of buttons) each.disabled = false
  }
}

const text = byId('text')
const sendButton = byId('send')
const fileInput = byId('files')

// the box grows with its text to 140 px; its borders count, so one line fits exactly
function fit() {
  text.style.height = 'auto'
  const borders = text.offsetHeight - text.clientHeight
  text.style.height = `${Math.min(text.scrollHeight + borders, 140)}px`
  sendButton.disabled = !text.value.trim() && picked.length === 0
}

// the files picked for the next message, above the box, each with its remove button
function drawPicked() {
  const box = byId('tray')
  box.replaceChildren()
  box.hidden = picked.length === 0 && !pickNote
  for (const item of picked) {
    const chip = el('div', 'chip')
    if (item.url) {
      const preview = el('img', 'thumb')
      preview.src = item.url
      preview.alt = ''
      chip.append(preview)
    } else {
      chip.append(el('span', 'mask file-mask'))
    }
    chip.append(el('span', 'name', item.file.name))
    const remove = button('', 'remove', () => {
      if (item.url) URL.revokeObjectURL(item.url)
      picked = picked.filter(each => each !== item)
      drawPicked()
      fit()
    })
    remove.setAttribute('aria-label', `Remove ${item.file.name}`)
    remove.append(el('span', 'mask remove-mask'))
    chip.append(remove)
    box.append(chip)
  }
  if (pickNote) box.append(el('div', 'note', pickNote))
}

// any file; past 200 MB it stays on the phone (a picture shrinks first, so the bound is the other files')
function pick(files) {
  const kept = []
  for (const file of files) {
    if (!isPicture(file) && file.size > UPLOAD_LIMIT) kept.push(file.name)
    else picked.push({ file, url: isPicture(file) ? URL.createObjectURL(file) : '' })
  }
  pickNote = kept.length > 0 ? `Over 200 MB, kept on the phone: ${kept.join(', ')}` : ''
  drawPicked()
  fit()
}

function clearPicked() {
  for (const item of picked) if (item.url) URL.revokeObjectURL(item.url)
  picked = []
  pickNote = ''
  drawPicked()
}

// a picture past Culpmit's screenshot size (long side 1,500 px, area 1.5 MP) goes at that size, a JPEG, or a PNG from
// a PNG; the Mac fits whatever arrives larger and turns other formats into a JPEG
async function shrink(file) {
  if (!isPicture(file) || file.type === 'image/gif') return file
  const url = URL.createObjectURL(file)
  try {
    const picture = new Image()
    picture.src = url
    await picture.decode()
    const w = picture.naturalWidth
    const h = picture.naturalHeight
    const scale = Math.min(1, LONG_SIDE / Math.max(w, h), Math.sqrt((0.667 * LONG_SIDE * LONG_SIDE) / (w * h)))
    if (scale >= 1 && /^image\/(jpeg|png|webp)$/.test(file.type)) return file
    const canvas = document.createElement('canvas')
    canvas.width = Math.max(1, Math.floor(w * scale))
    canvas.height = Math.max(1, Math.floor(h * scale))
    const context = canvas.getContext('2d')
    context.imageSmoothingQuality = 'high'
    context.drawImage(picture, 0, 0, canvas.width, canvas.height)
    const type = file.type === 'image/png' ? 'image/png' : 'image/jpeg'
    const blob = await new Promise(resolve => canvas.toBlob(resolve, type, 0.85))
    if (!blob) return file
    const stem = file.name.replace(/\.[^.]*$/, '') || 'photo'
    return new File([blob], `${stem}.${type === 'image/png' ? 'png' : 'jpg'}`, { type })
  } catch {
    return file
  } finally {
    URL.revokeObjectURL(url)
  }
}

// one file to the Mac, raw; the answer is the name it was saved under
async function upload(file, target) {
  const body = await shrink(file)
  const response = await fetch(`/api/upload?session=${encodeURIComponent(target)}&name=${encodeURIComponent(body.name)}`, {
    method: 'POST',
    headers: { ...headers(), 'Content-Type': body.type || 'application/octet-stream' },
    body,
  })
  if (!response.ok) throw new Error(`HTTP ${response.status}`)
  return (await response.json()).name
}

fileInput.addEventListener('change', () => {
  pick([...fileInput.files])
  fileInput.value = ''
})
text.addEventListener('input', fit)

// the message shows at once with Working under it; its files go first, then the words naming them. A failed send puts
// the words and the files back in the box.
byId('composer').addEventListener('submit', async event => {
  event.preventDefault()
  const words = text.value.trim()
  const files = picked.map(item => item.file)
  if ((!words && files.length === 0) || !session || isOpening) return
  let target = session
  sentCount += 1
  const entry = {
    key: `sent-${sentCount}`,
    session: target,
    text: words,
    names: files.map(file => file.name),
    isSaved: files.length === 0,
  }
  pending.push(entry)
  text.value = ''
  clearPicked()
  fit()
  isScrollDue = true
  if (chat) drawChat()
  try {
    // a new chat: the Mac opens its tab first, then the words and files go to it the usual way
    if (target === NEW) {
      isOpening = true
      const opened = await call('/api/new', {})
      isOpening = false
      if (!opened.session) throw new Error(STILL_OPENING)
      target = opened.session
      entry.session = target
      if (session === NEW) location.hash = target
    }
    const names = []
    for (const file of files) names.push(await upload(file, target))
    entry.names = names
    entry.isSaved = true
    const sent = await call('/api/send', { session: target, text: words, files: names })
    entry.id = sent.id
    refresh()
  } catch (error) {
    isOpening = false
    const isStillOpening = error.message === STILL_OPENING
    pending = pending.filter(each => each !== entry)
    if (session === target) {
      text.value = text.value ? `${words}\n${text.value}` : words
      for (const file of files) picked.push({ file, url: isPicture(file) ? URL.createObjectURL(file) : '' })
      if (isStillOpening) pickNote = STILL_OPENING
      drawPicked()
      fit()
      if (chat) drawChat()
    }
    if (!isStillOpening) bar('offline')
  }
})

// MARK: moving between the screens: home, Notifications, its gear, a chat

const VIEWS = ['list', 'notices', 'settings', 'chat']

function openView() {
  if (isUnpaired) return
  const place = decodeURIComponent(location.hash.slice(1))
  view = place === 'notifications' ? 'notices' : place === 'settings' ? 'settings' : place ? 'chat' : 'list'
  session = view === 'chat' ? place : ''
  for (const name of VIEWS) byId(name).hidden = name !== view
  chat = null
  chatKey = ''
  askId = ''
  stopAt = 0
  opened = new Set()
  earlier = { items: [], cursor: undefined, isLoading: false }
  notices = null
  noticesKey = ''
  pushNote = ''
  byId('messages').replaceChildren()
  rowNodes = new Map()
  byId('ask').hidden = true
  byId('switcher').hidden = true
  byId('modelPill').hidden = true
  closeRenamer()
  swipedOpen = null
  swiping = null
  clearPicked()
  fit()
  // an open chat counts its notices as read
  if (session && session !== NEW) call('/api/notices', { session }).catch(() => {})
  refresh()
}

byId('newChat').addEventListener('click', () => {
  location.hash = NEW
})
byId('openNotices').addEventListener('click', () => {
  location.hash = 'notifications'
})
byId('noticesBack').addEventListener('click', () => {
  location.hash = ''
})
byId('openSettings').addEventListener('click', () => {
  location.hash = 'settings'
})
byId('settingsBack').addEventListener('click', () => {
  location.hash = 'notifications'
})
byId('back').addEventListener('click', () => {
  location.hash = ''
})
// the sheet takes VoiceOver to its Done, and gives it back to the pill as it closes
byId('modelPill').addEventListener('click', () => {
  byId('switcher').hidden = false
  drawSwitcher()
  byId('switcherDone').focus()
})
function closeSwitcher() {
  byId('switcher').hidden = true
  byId('modelPill').focus()
}
byId('renameForm').addEventListener('submit', event => {
  event.preventDefault()
  saveName()
})
byId('renameCancel').addEventListener('click', closeRenamer)
byId('renamer').addEventListener('click', event => {
  if (event.target === event.currentTarget) closeRenamer()
})
byId('switcherDone').addEventListener('click', closeSwitcher)
// a tap on the dimmed chat around the sheet closes it too
byId('switcher').addEventListener('click', event => {
  if (event.target === event.currentTarget) closeSwitcher()
})
// the slider follows the finger stop by stop and goes to the chat on release; the polls leave it alone meanwhile
const effortInput = byId('effort')
effortInput.addEventListener('pointerdown', () => {
  isSliding = true
})
for (const type of ['pointerup', 'pointercancel']) {
  effortInput.addEventListener(type, () => {
    isSliding = false
  })
}
effortInput.addEventListener('input', () => drawLevel(Number(effortInput.value)))
effortInput.addEventListener('change', () => {
  isSliding = false
  const level = EFFORTS[Number(effortInput.value)]?.level
  const mine = wanted?.session === session ? wanted : null
  if (level && level !== (mine?.effort ?? chat?.effort)) askSwitch({ effort: level })
})
// a code block's copy button: the block's text to the clipboard, and a checkmark for a moment
const copiedTimers = new WeakMap()
byId('messages').addEventListener('click', async event => {
  const copy = event.target.closest('.copy')
  if (!copy) return
  try {
    await copyText(copy.closest('.code')?.dataset.raw ?? '')
  } catch {
    return
  }
  copy.classList.add('done')
  copy.setAttribute('aria-label', 'Copied')
  clearTimeout(copiedTimers.get(copy))
  copiedTimers.set(
    copy,
    setTimeout(() => {
      copy.classList.remove('done')
      copy.setAttribute('aria-label', 'Copy')
    }, 1500),
  )
})
byId('viewer').addEventListener('pointerdown', event => {
  const point = { x: event.clientX, y: event.clientY }
  if (fingers.size === 0) {
    const isSecond = isSecondTap(point)
    if (isSecond) clearTimeout(closing)
    press = { start: point, at: Date.now(), moved: false, pinched: false, isSecond }
  }
  fingers.set(event.pointerId, point)
  if (press && fingers.size > 1) press.pinched = true
  beginGesture()
})
byId('viewer').addEventListener('pointermove', event => {
  if (!fingers.has(event.pointerId)) return
  const point = { x: event.clientX, y: event.clientY }
  fingers.set(event.pointerId, point)
  if (press && Math.hypot(point.x - press.start.x, point.y - press.start.y) > 10) press.moved = true
  moveGesture()
})
function liftFinger(event) {
  if (!fingers.delete(event.pointerId)) return
  if (fingers.size > 0) {
    beginGesture()
    return
  }
  gesture = null
  clampZoom()
  drawZoom(true)
  const done = press
  press = null
  const isTap = event.type === 'pointerup' && done && !done.moved && !done.pinched && Date.now() - done.at < 350
  if (isTap) tapViewer({ x: event.clientX, y: event.clientY }, done.isSecond)
}
byId('viewer').addEventListener('pointerup', liftFinger)
byId('viewer').addEventListener('pointercancel', liftFinger)
// Safari's own pinch events would zoom the page under the viewer
byId('viewer').addEventListener('gesturestart', event => event.preventDefault())
byId('viewerClose').addEventListener('click', closeViewer)
window.addEventListener('hashchange', openView)

async function refresh() {
  if (isUnpaired) return
  if (isBusy) {
    isAgain = true
    return
  }
  isBusy = true
  if (!isReached) bar('loading')
  try {
    if (view === 'notices') {
      await loadNotices()
    } else if (session === NEW) {
      // the New chat screen draws as an empty chat, with zero earlier pages, until the Mac's tab registers
      chat = { session: NEW, title: NEW_TITLE, history: [], outbox: [], status: 'idle', now: Date.now() }
      earlier.cursor = null
      drawChat()
    } else if (session) {
      // the history comes along only when it changed since the one held here
      const held = chat?.session === session ? chat : null
      const since = held ? `&since=${held.historyAt ?? 0}` : ''
      const fresh = await call(`/api/chat?session=${encodeURIComponent(session)}${since}`)
      if (fresh.historySame && held) {
        fresh.history = held.history
        fresh.historyAt = held.historyAt
      }
      chat = fresh
      drawChat()
      // a short window (a chat just compacted, or busy with tools) brings one earlier page along by itself
      const talk = (chat.history ?? []).filter(m => m.role === 'user' || m.role === 'assistant').length
      if (earlier.cursor === undefined && !earlier.isLoading && talk < 20) loadEarlier()
    } else {
      state = await call('/api/state')
      if (view === 'settings') {
        await drawSettings()
      } else {
        drawLimits()
        drawBadge()
        drawChats()
        drawRecent()
      }
    }
    isReached = true
    bar('')
  } catch (error) {
    // a chat that closed sends the view back to the list
    if (session && String(error.message).includes('404')) location.hash = ''
    else bar(isUnpaired ? '' : 'offline')
  } finally {
    isBusy = false
    if (isAgain) {
      isAgain = false
      refresh()
    }
  }
}

if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('/sw.js').catch(() => {})
  navigator.serviceWorker.addEventListener('message', event => {
    if (event.data?.open) location.hash = new URL(event.data.open).hash
  })
}

setInterval(() => {
  if (document.visibilityState === 'visible') refresh()
}, 3000)
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'visible') refresh()
})
// a phone with its own key opens the chats; one holding the lasting code opens them too and trades it by itself; one
// yet to pair shows the pairing field, the QR link's digits in it
if (isSixDigits(pairing)) {
  openView()
  pairWith(pairing)
} else if (pairing) openView()
else {
  showPairing()
  if (linkedCode) byId('pairCode').value = `${linkedCode.slice(0, 3)} ${linkedCode.slice(3)}`
}
