import type { ApiContentBlock, EngineInterface, Register, SessionMessage, Timer, ToolUseSummary } from 'claude-code'

// Uselim's phone relay inside a chat. The phone reaches the session through files Uselim keeps in
// ~/Library/Application Support/Uselim/relay, and through Uselim's local server, which holds a question
// or a permission ask open until the phone answers it:
//   chats/<session>.json     the chat: folder, opening words, working or idle, the main loop's running tools,
//                            its last sign of life, its model and effort, and when it last took a switch from the phone
//   history/<session>.json   the recent items: the person's words, the answers, and a row per tool call with its
//                            target, state and output; rewritten as tools run and as each turn ends
//   images/<session>/        the pictures in the conversation as the transcript stores them, <key>.json each
//                            ({ mediaType, data } in base64), and index.json, which item holds which
//   replies/<session>.json   the main loop's last answer
//   inbox/<session>.json     messages from the phone, { messages: [{ id, text, at }] }, ids in sending order
//   inbox/<session>.ack      the id of the last message this chat took from the inbox
//   inbox/<session>.entered  the id of the last phone message the history holds, written with the first history that
//                            shows its words (a moment into its turn), at the latest as that turn ends
//   inbox/<session>.stop     { at }: the phone's Stop for the running turn, emptied once read
//   inbox/<session>.switch   { model, effort, at }: the phone's switch of the chat's model or effort, emptied once read;
//                            the chat's own /model and /effort run it, which keep the change to this chat
//   inbox/<session>.rename   { title, at }: the phone's rename, emptied once read; the chat's own /rename runs it
//   asks/<session>.json      a question or a permission ask held for the phone; empty once settled
//   files/<session>/         files and photos from the phone, Uselim's to write; a message names each by its path
//   away.json                { away, at }: Uselim's reading of the Mac, rewritten every half minute
//   local.key                the key every call to Uselim's server carries from the Mac, made by Uselim
// A message from the phone enters as the person's own words, the same as a prompt typed in the panel.
// A question goes to the dialog and to the phone at once, wherever the person is, and the first answer stands; a
// permission ask goes to the phone while Uselim reads the Mac as away.

const SERVER = 'http://127.0.0.1:47472'
const HEADERS = { 'X-Uselim': '1' }
const POLL_MS = 2_000
const ALIVE_MS = 20_000
const RETRY_MS = 30_000
const AWAY_FRESH_MS = 90_000
const STOP_FRESH_MS = 30_000
// the window: the newest stretch of the chat, 60 messages or 120 tool rows, whichever comes first; the phone reads
// what came before from the transcript through Uselim
const WINDOW_TALK = 60
const WINDOW_TOOLS = 120
const LONGEST = 12_000
const DETAIL = 600
const OUTPUT = 500
const HISTORY_SOON_MS = 1_500
// the row a Stop from the phone leaves after the turn it ended, for the model, where the panel's own Stop leaves its
// interruption line
const STOPPED = 'The person stopped the turn above from the phone before it finished.'

type Status = 'working' | 'idle' | 'ended'
type Chat = {
  session: string
  cwd: string
  project: string
  began: string
  status: Status
  tools: string[]
  at: number
  model?: string
  effort?: string
  switchedAt?: number
}
type Message = { id: string; text: string }
// a switch from the phone: a model alias, an effort level, and the phone's time of asking, handed back once taken
type Switch = { model?: string; effort?: string; at?: number }
type Inbox = { messages?: Message[] }
type Ask =
  | { kind: 'question'; id: string; questions: unknown }
  | { kind: 'permission'; id: string; tool: string; detail: string; more?: number }
type Answer = { answers?: Record<string, string>; allow?: boolean; isStopped?: true }
type Item =
  | { role: 'user'; text: string; images?: string[] }
  | { role: 'command'; text: string; output?: string }
  | { role: 'assistant'; text: string }
  | {
      role: 'tool'
      id: string
      tool: string
      target: string
      detail: string
      state: 'running' | 'done' | 'error'
      output?: string
      images?: string[]
    }
type Picture = { mediaType: string; data: string }
// which item holds which pictures: a tool's result by its call id, a prompt's by its words, prompts in order
type Pictures = { tools: Record<string, string[]>; prompts: { words: string; images: string[] }[] }

let dir = ''
let chat: Chat | undefined
let delivered = ''
let entered = '' // the last phone message whose turn has started
let enteredWords = '' // that message's batch as the history words it
let marked = '' // the entered mark last written
let queue: Message[] = []
// the chat's slash commands by name, read at the start and every minute; the output of each the phone ran, by its line
let commandNames = new Set<string>()
let commandsRead = 0
const outputs = new Map<string, string>()
let isSubmitting = false
let isPolling = false
let lastAlive = 0
let lastRefusal = 0
let turn = '' // the main loop's running turn, the one a Stop ends
let isStopping = false // a Stop from the phone is ending the running turn
const running = new Map<string, string>() // the main loop's tool calls in flight: call id to tool name
let pictures: Pictures = { tools: {}, prompts: [] }
let isWritingHistory = false
let isHistoryAgain = false
let historyTimer: Timer | undefined
let switching: Promise<void> | undefined // the phone's switch running, a command at a time
let nextSwitch: Switch | undefined // a switch read meanwhile, joined into one

// the text with the tags the panel wraps around a prompt taken out; line breaks stay
const clean = (text: string) =>
  text
    .replace(/<([a-z_-]+)[^>]*>[\s\S]*?<\/\1>/g, ' ')
    .replace(/<[^>]+>/g, ' ')
    .replace(/[^\S\n]+/g, ' ')
    .replace(/ *\n */g, '\n')
    .replace(/\n{3,}/g, '\n\n')
    .trim()

const oneLine = (text: string) => clean(text).replace(/\s+/g, ' ')

const projectOf = (cwd: string) => cwd.split('/').filter(Boolean).at(-1) ?? cwd

// a permission's call in full, as Uselim's --permission details it, so Allow on the phone answers for what it reads: a
// command, a written file's path and content, an edit's path and its lines marked - and +, a notebook cell's new
// source, a search with its folder, an agent's prompt, any other input as JSON. The card holds the first CARD_LONGEST
// characters and counts the rest, which the Mac's dialog shows.
const CARD_LONGEST = 100_000

function cardDetail(tool: string, input: unknown) {
  const fields = (input ?? {}) as Record<string, unknown>
  const field = (key: string) => (typeof fields[key] === 'string' ? (fields[key] as string) : '')
  const marked = (text: string, mark: string) => text.split('\n').map(line => `${mark} ${line}`).join('\n')
  const whole = (() => {
    switch (tool) {
      case 'Bash':
        return field('command')
      case 'Read':
        return field('file_path')
      case 'Write':
        return `${field('file_path')}\n\n${field('content')}`
      case 'Edit':
        return `${field('file_path')}\n\n${marked(field('old_string'), '-')}\n${marked(field('new_string'), '+')}`
      case 'NotebookEdit': {
        // the edit's kind first, so a cell's deletion reads as one
        const kind = field('edit_mode') || 'replace'
        const cell = [kind, 'cell', field('cell_id')].filter(Boolean).join(' ')
        return `${field('notebook_path')}\n\n${cell}${kind === 'delete' ? '' : `\n${field('new_source')}`}`
      }
      case 'Grep':
      case 'Glob':
        return [field('pattern'), field('path')].filter(Boolean).join('\nin ')
      case 'WebFetch':
        return field('url')
      case 'WebSearch':
        return field('query')
      case 'Agent':
      case 'Task':
        return field('prompt')
      default:
        return JSON.stringify(fields, null, 2)
    }
  })()
  const detail = whole.slice(0, CARD_LONGEST)
  return { detail, more: whole.length - detail.length }
}

async function save($: EngineInterface) {
  if (!chat) return
  chat.at = await $.clock.now()
  lastAlive = chat.at
  await $.fs.write(`${dir}/chats/${chat.session}.json`, JSON.stringify(chat))
}

async function showTools($: EngineInterface) {
  if (!chat) return
  chat.tools = [...new Set(running.values())]
  await save($).catch(() => undefined)
}

async function messages($: EngineInterface): Promise<SessionMessage[]> {
  const found = await $.session.messages()
  return Array.isArray(found) ? found : []
}

// a continued chat opens on the engine's summary of the conversation before it
const SUMMARY = /^This session is being continued from a previous conversation/

// the opening words: the first thing typed in the chat, the way Uselim's menu names a chat
async function opening($: EngineInterface) {
  for (const message of await messages($)) {
    const text = message.role === 'user' ? oneLine(message.text) : ''
    if (text && !SUMMARY.test(text)) return text.slice(0, 60)
  }
  return ''
}

const cut = (text: string, length: number) => (text.length > length ? `${text.slice(0, length)} …` : text)

// a long output keeps its opening and its end, where a command's error lands
const excerpt = (text: string) =>
  text.length > OUTPUT ? `${text.slice(0, OUTPUT * 0.6)}\n…\n${text.slice(-OUTPUT * 0.4)}` : text

// what a tool row shows: a short target on the row, the call in full beneath it once opened
function describeTool(tool: string, input: Record<string, unknown>) {
  const field = (key: string) => (typeof input[key] === 'string' ? (input[key] as string) : '')
  const file = field('file_path') || field('notebook_path')
  const name = file.split('/').filter(Boolean).at(-1) ?? file
  const lines = (text: string, mark: string) => text.split('\n').map(line => `${mark} ${line}`).join('\n')
  switch (tool) {
    case 'Bash':
      return { target: field('command').split('\n')[0] ?? '', detail: field('command') }
    case 'Read':
    case 'NotebookEdit':
      return { target: name, detail: file }
    case 'Write':
      return { target: name, detail: `${file}\n\n${field('content')}` }
    case 'Edit':
      return { target: name, detail: `${file}\n\n${lines(field('old_string'), '-')}\n${lines(field('new_string'), '+')}` }
    case 'Grep':
    case 'Glob':
      return { target: field('pattern'), detail: [field('pattern'), field('path')].filter(Boolean).join('\nin ') }
    case 'WebFetch':
      return { target: field('url'), detail: field('url') }
    case 'WebSearch':
      return { target: field('query'), detail: field('query') }
    case 'Agent':
    case 'Task':
      return { target: field('description'), detail: field('prompt') }
    default: {
      const first = Object.values(input).find(value => typeof value === 'string') as string | undefined
      return { target: (first ?? '').split('\n')[0] ?? '', detail: JSON.stringify(input, null, 1) }
    }
  }
}

function toolItem(use: ToolUseSummary): Item {
  const { target, detail } = describeTool(use.tool, use.input ?? {})
  const state = use.text === undefined ? 'running' : use.isError ? 'error' : 'done'
  const output = use.text?.trim() ? excerpt(use.text.trim()) : undefined
  const images = pictures.tools[use.tool_use_id]
  return {
    role: 'tool',
    id: use.tool_use_id,
    tool: use.tool,
    target: cut(target, 160),
    detail: cut(detail, DETAIL),
    state,
    ...(output ? { output } : {}),
    ...(images ? { images } : {}),
  }
}

// the pictures pasted into prompts go to the prompts with the same words, newest to newest
function promptPictures(rows: SessionMessage[]) {
  const found = new Map<number, string[]>()
  let next = pictures.prompts.length - 1
  for (let index = rows.length - 1; index >= 0 && next >= 0; index -= 1) {
    const row = rows[index]
    if (!row || row.role !== 'user' || (row.toolResults?.length ?? 0) > 0) continue
    const prompt = pictures.prompts[next]
    if (prompt && oneLine(row.text) === prompt.words) {
      found.set(index, prompt.images)
      next -= 1
    }
  }
  return found
}

function windowOf(items: Item[]) {
  let talk = 0
  let tools = 0
  let start = items.length
  while (start > 0) {
    const item = items[start - 1]
    if (item?.role === 'tool' ? tools === WINDOW_TOOLS : talk === WINDOW_TALK) break
    if (item?.role === 'tool') tools += 1
    else talk += 1
    start -= 1
  }
  return items.slice(start)
}

const COMMAND = /<command-name>([^<]*)<\/command-name>[\s\S]*?(?:<command-args>([\s\S]*?)<\/command-args>|$)/
const STDOUT = /<local-command-stdout>([\s\S]*?)<\/local-command-stdout>/

// one writer at a time; a write asked for meanwhile runs after it
async function writeHistory($: EngineInterface): Promise<void> {
  if (!chat) return
  if (isWritingHistory) {
    isHistoryAgain = true
    return
  }
  isWritingHistory = true
  try {
    const rows = await messages($)
    const pasted = promptPictures(rows)
    const items: Item[] = []
    rows.forEach((row, index) => {
      // a slash command's row, as the panel shows one: its line, and its output beneath when the chat records it
      const command = row.role === 'user' ? COMMAND.exec(row.text) : null
      if (command) {
        const line = commandLine((command[1] ?? '').replace(/^\//, ''), (command[2] ?? '').trim())
        items.push({ role: 'command', text: line, ...(outputs.has(line) ? { output: outputs.get(line) } : {}) })
        return
      }
      const stdout = row.role === 'user' ? STDOUT.exec(row.text) : null
      if (stdout) {
        const last = items.at(-1)
        if (last?.role === 'command' && stdout[1]?.trim()) last.output = cut(clean(stdout[1]), OUTPUT)
        return
      }
      if (row.role === 'user') {
        const text = clean(row.text)
        const images = pasted.get(index)
        if ((text && !SUMMARY.test(text)) || images) {
          items.push({ role: 'user', text: SUMMARY.test(text) ? '' : cut(text, LONGEST), ...(images ? { images } : {}) })
        }
        return
      }
      const text = row.text.trim()
      if (text) items.push({ role: 'assistant', text: cut(text, LONGEST) })
      for (const use of row.toolUses ?? []) items.push(toolItem(use))
    })
    if (!chat) return
    await $.fs.write(
      `${dir}/history/${chat.session}.json`,
      JSON.stringify({ at: await $.clock.now(), messages: windowOf(items) }),
    )
    await markEntered($, items)
  } finally {
    isWritingHistory = false
    if (isHistoryAgain) {
      isHistoryAgain = false
      await writeHistory($)
    }
  }
}

// the phone draws its own copy of a message until the history holds it: the mark goes out with the first history whose
// latest words are that message's, so the two copies hand over at once while its turn runs
async function markEntered($: EngineInterface, items: Item[]) {
  if (!chat || !entered || entered === marked) return
  let words: string | undefined
  for (let index = items.length - 1; index >= 0 && words === undefined; index -= 1) {
    const item = items[index]
    if (item?.role === 'user') words = item.text
  }
  if (words !== enteredWords) return
  marked = entered
  await $.fs.write(`${dir}/inbox/${chat.session}.entered`, entered)
}

// while a turn runs, the history follows its tools a moment behind
function historySoon($: EngineInterface) {
  if (historyTimer) return
  historyTimer = $.clock.after(HISTORY_SOON_MS, () => {
    historyTimer = undefined
    writeHistory($).catch(() => undefined)
  })
}

function picturesIn(content: unknown): Picture[] {
  if (!Array.isArray(content)) return []
  const found: Picture[] = []
  for (const block of content as ApiContentBlock[]) {
    const source = block.source as { type?: unknown; media_type?: unknown; data?: unknown } | undefined
    if (block.type !== 'image' || source?.type !== 'base64' || typeof source.data !== 'string') continue
    found.push({ mediaType: typeof source.media_type === 'string' ? source.media_type : 'image/png', data: source.data })
  }
  return found
}

const safeKey = (text: string) => text.replace(/[^A-Za-z0-9_-]/g, '-').slice(0, 80)

async function savePictures($: EngineInterface, key: string, found: Picture[]) {
  if (!chat) return []
  const keys: string[] = []
  for (const [index, picture] of found.entries()) {
    const name = `${safeKey(key)}-${index}`
    await $.fs.write(`${dir}/images/${chat.session}/${name}.json`, JSON.stringify(picture))
    keys.push(name)
  }
  return keys
}

async function loadPictures($: EngineInterface, session: string) {
  const text = await $.fs.read(`${dir}/images/${session}/index.json`).catch(() => '')
  const read = text ? (JSON.parse(text) as Partial<Pictures>) : {}
  pictures = { tools: read.tools ?? {}, prompts: read.prompts ?? [] }
}

async function writePictures($: EngineInterface) {
  if (!chat) return
  const tools = Object.entries(pictures.tools).slice(-300)
  pictures = { tools: Object.fromEntries(tools), prompts: pictures.prompts.slice(-60) }
  await $.fs.write(`${dir}/images/${chat.session}/index.json`, JSON.stringify(pictures))
}

async function readAck($: EngineInterface, session: string) {
  return (await $.fs.read(`${dir}/inbox/${session}.ack`).catch(() => '')).trim()
}

// a phone message that names one of the chat's slash commands: the command's name and its arguments; any other
// message, a pasted path among them, goes as words
function commandIn(text: string) {
  const found = /^\/(\S+)(?:\s+([\s\S]*))?$/.exec(text.trim())
  return found?.[1] && commandNames.has(found[1]) ? { name: found[1], args: (found[2] ?? '').trim() } : undefined
}

// a slash command from the phone runs as the chat's own, as if typed in the panel; it starts no turn for the history
// to show it by, so the phone's copy gives way at once, and the chat keeps its output for the history's row
async function runCommand($: EngineInterface, message: Message, command: { name: string; args: string }) {
  if (!chat) return
  entered = message.id
  enteredWords = ''
  marked = message.id
  await $.fs.write(`${dir}/inbox/${chat.session}.entered`, message.id)
  const ran = await $.command.run({ command: command.name, args: command.args }).catch(() => undefined)
  if (ran?.text) {
    outputs.set(commandLine(command.name, command.args), cut(ran.text.trim(), OUTPUT))
    if (outputs.size > 30) outputs.delete(outputs.keys().next().value as string)
  }
  // a /model from the phone shows on the model pill at once
  if (chat) {
    chat.model = (await $.session.model().catch(() => undefined)) ?? chat.model
    await save($)
  }
  await writeHistory($)
}

const commandLine = (name: string, args: string) => `/${name}${args ? ` ${args}` : ''}`

// one submission at a time: texts that arrive meanwhile enter together after it; a refused one
// (after an interrupt, until the next typed prompt) waits half a minute and goes again. A slash command goes alone,
// in its place among them.
function pump($: EngineInterface, now: number) {
  if (isSubmitting || queue.length === 0 || now - lastRefusal < RETRY_MS) return
  const first = queue[0]
  const command = first ? commandIn(first.text) : undefined
  if (first && command) {
    queue.shift()
    isSubmitting = true
    runCommand($, first, command)
      .catch(() => undefined)
      .finally(() => {
        isSubmitting = false
      })
    return
  }
  const next = queue.findIndex(m => commandIn(m.text) !== undefined)
  const batch = next > 0 ? queue.slice(0, next) : queue
  queue = next > 0 ? queue.slice(next) : []
  isSubmitting = true
  const refused = async () => {
    isSubmitting = false
    queue.unshift(...batch)
    lastRefusal = await $.clock.now()
  }
  // the call resolves as the batch's turn starts; the history shows it a moment later. The engine turns away a plugin's
  // text that opens with a slash (after trimming), which would run a command as the person: words that only look like
  // one, a pasted path, go as words behind a zero-width space, the text unchanged to the eye
  const joined = batch.map(m => m.text).join('\n\n')
  const text = joined.trimStart().startsWith('/') ? `\u200b${joined.trimStart()}` : joined
  $.prompt.submit({ text, asUser: true }).then(async result => {
    if (result.drop !== undefined) return refused()
    isSubmitting = false
    entered = batch.at(-1)?.id ?? entered
    enteredWords = cut(clean(text), LONGEST)
  }, refused)
}

// ends the main loop's running turn with its tools; its turn.complete leaves STOPPED for the model
async function stop($: EngineInterface) {
  if (!turn) return
  const ending = turn
  turn = ''
  isStopping = true
  await $.turn.abort({ turnId: ending }).catch(() => {
    isStopping = false
  })
}

// the phone's Stop, read once; one older than half a minute lapses
async function takeStop($: EngineInterface, session: string, now: number) {
  const path = `${dir}/inbox/${session}.stop`
  const text = await $.fs.read(path).catch(() => '')
  if (!text.trim()) return
  await $.fs.write(path, '')
  const asked = (JSON.parse(text) as { at?: number }).at ?? 0
  if (now - asked < STOP_FRESH_MS) await stop($)
}

// the effort /effort reports it set: the level asked, or the one a cap lowered it to; undefined when it set none (an
// environment variable in charge, a refusal), and the chat's next request tells
function effortTaken(text: string | undefined, asked: string) {
  if (!text) return undefined
  const capped = /set to '([a-z]+)' instead/.exec(text)
  if (capped) return capped[1]
  return text.startsWith('Set effort level') ? asked : undefined
}

// the phone's switches, one after another: the model first, since the levels an effort takes depend on it. The engine
// queues each command until the session is idle, so a switch asked mid-turn lands as the turn ends.
async function runSwitches($: EngineInterface) {
  while (nextSwitch && chat) {
    const asked = nextSwitch
    nextSwitch = undefined
    if (asked.model) await $.command.run({ command: 'model', args: asked.model }).catch(() => undefined)
    if (asked.effort) {
      const ran = await $.command.run({ command: 'effort', args: asked.effort }).catch(() => undefined)
      const taken = effortTaken(ran?.text, asked.effort)
      if (taken && chat) chat.effort = taken
    }
    if (!chat) return
    chat.model = (await $.session.model().catch(() => undefined)) ?? chat.model
    chat.switchedAt = asked.at ?? (await $.clock.now())
    await save($)
  }
}

// the phone's switch, read once and joined to any still waiting; the poll moves on while it runs
async function takeSwitch($: EngineInterface, session: string) {
  const path = `${dir}/inbox/${session}.switch`
  const text = await $.fs.read(path).catch(() => '')
  if (!text.trim()) return
  await $.fs.write(path, '')
  nextSwitch = { ...nextSwitch, ...(JSON.parse(text) as Switch) }
  switching ??= runSwitches($)
    .catch(() => undefined)
    .finally(() => {
      switching = undefined
    })
}

// the phone's rename, read once: the chat's own /rename, as the panel's rename tells the chat, so the name it keeps in
// memory and writes again is the new one; the engine runs it once the session is idle
async function takeRename($: EngineInterface, session: string) {
  const path = `${dir}/inbox/${session}.rename`
  const text = await $.fs.read(path).catch(() => '')
  if (!text.trim()) return
  await $.fs.write(path, '')
  const title = ((JSON.parse(text) as { title?: string }).title ?? '').replace(/\s+/g, ' ').trim()
  if (title) $.command.run({ command: 'rename', args: title }).catch(() => undefined)
}

async function readCommands($: EngineInterface, now: number) {
  commandsRead = now
  const found = await $.command.list().catch(() => undefined)
  if (found) commandNames = new Set(found.map(command => command.name))
}

async function poll($: EngineInterface) {
  if (!chat || isPolling) return
  isPolling = true
  try {
    const now = await $.clock.now()
    // a /clear opens a new conversation under a new id, and session.start runs at launch alone
    const id = await $.session.id()
    if (id !== chat.session) {
      chat = { ...chat, session: id, began: '', status: 'idle', tools: [] }
      delivered = await readAck($, id)
      entered = ''
      enteredWords = ''
      marked = ''
      await loadPictures($, id)
      await save($)
    }
    await takeStop($, id, now)
    await takeSwitch($, id)
    await takeRename($, id)
    if (now - commandsRead > 60_000) await readCommands($, now)
    const text = await $.fs.read(`${dir}/inbox/${id}.json`).catch(() => '')
    const fresh = text ? ((JSON.parse(text) as Inbox).messages ?? []).filter(m => m.id > delivered) : []
    if (fresh.length > 0) {
      delivered = fresh.at(-1)?.id ?? delivered
      await $.fs.write(`${dir}/inbox/${id}.ack`, delivered)
      queue.push(...fresh.map(m => ({ id: m.id, text: m.text })))
    }
    pump($, now)
    if (now - lastAlive > ALIVE_MS) await save($)
  } catch {
    // a half-written inbox reads whole on the next round
  } finally {
    isPolling = false
  }
}

async function isAway($: EngineInterface) {
  const text = await $.fs.read(`${dir}/away.json`).catch(() => '')
  if (!text) return false
  const away = JSON.parse(text) as { away?: boolean; at?: number }
  return away.away === true && (await $.clock.now()) - (away.at ?? 0) < AWAY_FRESH_MS
}

// holds an ask for the phone: Uselim's server answers each wait within about 20 s with the phone's
// answer, "desk" once the person is back at the Mac (a permission's wait), "stopped" after the phone's Stop, or
// "waiting"; a permission's hour of waits ends at the dialog. A question's wait (`always`) runs beside the dialog for
// as long as the dialog stays open, so a question asked overnight is still on the phone in the morning; it ends once
// `isDone` says the dialog answered, at a Stop, or as the dispatch ends.
async function waitFor(
  $: EngineInterface,
  ask: Ask,
  signal: AbortSignal,
  options: { always?: boolean; isDone?: () => boolean } = {},
): Promise<Answer | undefined> {
  if (!chat) return undefined
  const path = `${dir}/asks/${chat.session}.json`
  await $.fs.write(
    path,
    JSON.stringify({ ...ask, project: chat.project, began: chat.began, at: await $.clock.now() }),
  )
  try {
    const always = options.always ? '&always=1' : ''
    const url = `${SERVER}/relay/wait?session=${encodeURIComponent(chat.session)}&ask=${encodeURIComponent(ask.id)}${always}`
    // the server holds each wait 20 s before "waiting", which paces the rounds; a "waiting" back within 10 s comes
    // from something other than Uselim on the port, and counts toward 180 rounds, a question's cap alone
    const rounds = options.always ? Number.POSITIVE_INFINITY : 180
    let quick = 0
    for (let round = 0; round < rounds && quick < 180 && !signal.aborted && !options.isDone?.(); round += 1) {
      // the local key, once Uselim has made it; an older Uselim takes the header alone
      const key = (await $.fs.read(`${dir}/local.key`).catch(() => '')).trim()
      const headers = key ? { ...HEADERS, 'X-Uselim-Key': key } : HEADERS
      const sent = await $.clock.now()
      const reply = await $.http.fetch(url, { headers }).catch(() => undefined)
      if (!reply?.ok) return undefined
      const body = JSON.parse(reply.text) as { state?: string; answer?: Answer }
      if (body.state === 'answered') return body.answer
      if (body.state === 'stopped') {
        await stop($)
        return { isStopped: true }
      }
      if (body.state !== 'waiting') return undefined
      if ((await $.clock.now()) - sent < 10_000) quick += 1
    }
    return undefined
  } finally {
    // the card goes, unless a newer ask took its place meanwhile
    const held = await $.fs.read(path).catch(() => '')
    if (!held || (JSON.parse(held) as { id?: string }).id === ask.id) await $.fs.write(path, '')
  }
}

// registers the chat and starts the inbox poll, once: at the session's start, or at the first turn of a chat that
// loaded the mod mid-session (an install, then /reload-plugins)
async function ensure($: EngineInterface, cwd?: string) {
  if (chat) return
  const home = await $.env.get('HOME')
  if (!home) return
  dir = `${home}/Library/Application Support/Uselim/relay`
  const session = await $.session.id()
  // the folder the chat started in, which a shell's cd leaves alone: Claude Code keeps the transcript under it
  const where = cwd ?? (await $.session.root())
  chat = { session, cwd: where, project: projectOf(where), began: await opening($), status: 'idle', tools: [], at: 0 }
  chat.model = await $.session.model().catch(() => undefined)
  delivered = await readAck($, session)
  await loadPictures($, session)
  await save($)
  await writeHistory($)
  $.clock.every(POLL_MS, () => poll($))
}

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    const started = await next(e)
    await ensure($, e.cwd)
    return started
  })

  on('turn.start', async ($, e, next) => {
    if (e.agentId === undefined) await ensure($)
    if (chat && e.agentId === undefined) {
      turn = e.turnId
      running.clear()
      chat.status = 'working'
      chat.tools = []
      if (!chat.began) chat.began = oneLine(e.text).slice(0, 60)
      // a model switched at the Mac shows on the phone from the next turn
      chat.model = (await $.session.model().catch(() => undefined)) ?? chat.model
      await save($)
      historySoon($)
    }
    return next(e)
  })

  // the effort the main loop's requests go out at, as the session sets it (the Mac's panel, /effort, the model's
  // default); read, never rewritten
  on('turn.step', async function* ($, e, next) {
    if (chat && e.agentId === undefined && typeof e.effort === 'string' && e.effort !== chat.effort) {
      chat.effort = e.effort
      await save($).catch(() => undefined)
    }
    return yield* next(e)
  })

  on('turn.complete', async ($, e, next) => {
    const result = await next(e)
    if (e.agentId === undefined) await ensure($)
    if (chat && e.agentId === undefined) {
      if (e.turnId === turn) turn = ''
      if (isStopping && e.reason === 'aborted') {
        await $.session.append({ message: { type: 'user', content: [{ type: 'text', text: STOPPED }] } })
      }
      isStopping = false
      running.clear()
      chat.status = 'idle'
      chat.tools = []
      await save($)
      await writeHistory($)
      if (entered) {
        marked = entered
        await $.fs.write(`${dir}/inbox/${chat.session}.entered`, entered)
      }
      await $.fs.write(
        `${dir}/replies/${chat.session}.json`,
        JSON.stringify({ text: e.answer, reason: e.reason, turn: e.turnId, at: await $.clock.now() }),
      )
    }
    return result
  })

  on('session.end', async ($, e, next) => {
    if (chat) {
      chat.status = 'ended'
      await save($)
    }
    return next(e)
  })

  // the main loop's running tools by name, for the phone's Working line, and its tool rows a moment behind
  on('tool.call', async ($, e, next) => {
    if (e.agentId !== undefined || !chat) return next(e)
    const id = e.tool_use_id ?? `t${await $.clock.now()}`
    running.set(id, e.tool)
    await showTools($)
    historySoon($)
    try {
      return await next(e)
    } finally {
      running.delete(id)
      await showTools($)
      historySoon($)
    }
  })

  // pictures as the transcript stores them: what a tool handed the model (a screenshot it read) by the call's id
  on('session.append', { door: 'tool-result' }, async ($, e, next) => {
    const stored = await next(e)
    if (e.agentId !== undefined || !chat) return stored
    let isFound = false
    for (const block of e.message.content) {
      const id = typeof block.tool_use_id === 'string' ? block.tool_use_id : ''
      const found = block.type === 'tool_result' && id ? picturesIn(block.content) : []
      if (found.length === 0) continue
      pictures.tools[id] = await savePictures($, id, found)
      isFound = true
    }
    if (isFound) {
      await writePictures($)
      historySoon($)
    }
    return stored
  })

  // and a picture pasted into a prompt, by the prompt's words
  on('session.append', { door: 'prompt' }, async ($, e, next) => {
    const stored = await next(e)
    const found = e.agentId === undefined && chat ? picturesIn(e.message.content) : []
    if (found.length === 0) return stored
    const words = e.message.content
      .map(block => (block.type === 'text' && typeof block.text === 'string' ? block.text : ''))
      .join('\n')
    pictures.prompts.push({ words: oneLine(words), images: await savePictures($, e.uuid, found) })
    await writePictures($)
    historySoon($)
    return stored
  })

  // the question answered from the phone stands in for the dialog: the hook answers for the tool, and the engine maps
  // the result for the model with the tool's own mapper. tool.call sits above the engine's own PreToolUse handling,
  // which the built-in security plugin carries past the plugins beneath it.
  // a question goes to the dialog and to the phone at once, wherever the person is; the first answer stands. An answer
  // from the phone returns with the dialog still open, which ends the dialog.
  on('tool.call', { tool: 'AskUserQuestion' }, async ($, e, next) => {
    if (e.tool !== 'AskUserQuestion' || e.agentId !== undefined || !chat) return next(e)
    const id = e.tool_use_id ?? `q${await $.clock.now()}`
    let isAnswered = false
    const dialog = next(e).then(result => {
      isAnswered = true
      return { from: 'dialog' as const, result }
    })
    const phone = waitFor($, { kind: 'question', id, questions: e.questions }, next.signal, {
      always: true,
      isDone: () => isAnswered,
    }).then(answer => ({ from: 'phone' as const, answer }))
    const first = await Promise.race([dialog, phone])
    if (first.from === 'dialog') return first.result
    // a Stop ended the turn, and the dispatch has already moved on past this hook
    if (first.answer?.isStopped || next.signal.aborted) return { deny: 'The turn was stopped.' }
    if (first.answer?.answers) {
      isAnswered = true
      return { result: { questions: e.questions, answers: first.answer.answers } }
    }
    return (await dialog).result
  })

  on('classic.PermissionRequest', async ($, e, next) => {
    if (!(await isAway($))) return next(e)
    const id = `p${await $.clock.now()}`
    const { detail, more } = cardDetail(e.tool_name, e.tool_input)
    const ask: Ask = { kind: 'permission', id, tool: e.tool_name, detail, ...(more > 0 ? { more } : {}) }
    const answer = await waitFor($, ask, next.signal)
    if (answer?.isStopped || next.signal.aborted) {
      return { decision: { behavior: 'deny' as const, message: 'The turn was stopped.' } }
    }
    if (answer?.allow === undefined) return next(e)
    return answer.allow
      ? { decision: { behavior: 'allow' as const } }
      : { decision: { behavior: 'deny' as const, message: 'Denied from the phone.' } }
  })
}
