import { expect, mock, test } from 'claude-code/testing'
import type { On } from 'claude-code'

// The relay's files live in memory here; every path reads as empty until written, as the mod treats a missing file.
const HOME = '/test/home'
const RELAY = `${HOME}/Library/Application Support/Uselim/relay`
const SESSION = 'session-0001-test'
const QUESTION = {
  question: 'Which phone app?',
  header: 'Phone app',
  options: [
    { label: 'Web app', description: 'free' },
    { label: 'Native', description: 'Xcode' },
  ],
  multiSelect: false,
}

const OPENING = [{ role: 'user' as const, text: 'can we build a phone relay?', toolUses: [] }]

// what /effort prints for a level: set as asked, by default
const EFFORT_SET = (level: string) => `Set effort level to ${level} (this session only): Comprehensive implementation`

function world(on: On, now: number, rows: unknown[] = OPENING, effortText = EFFORT_SET) {
  const files = new Map<string, string>()
  const submitted: { text: string; origin?: unknown }[] = []
  const aborted: string[] = []
  const ran: [string, string][] = []
  // prompts and commands in the order the chat took them
  const order: string[] = []
  let model = 'claude-opus-5-5'
  const clock = mock.clock(on, { now })
  mock.env(on, { HOME })
  on('fs.read', (_, e) => ({ value: files.get(e.path) ?? '' }))
  on('fs.write', (_, e) => {
    files.set(e.path, e.text)
    return { value: undefined }
  })
  on('session.id', () => ({ value: SESSION }))
  on('session.messages', () => ({ value: rows as never }))
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  on('turn.start', (_, e) => ({ turnId: e.turnId }))
  on('turn.complete', (_, e) => ({ text: e.answer }))
  on('turn.abort', (_, e) => {
    aborted.push(e.turnId)
    return { value: undefined }
  })
  on('prompt.submit', (_, e) => {
    submitted.push({ text: e.text, origin: e.origin })
    order.push(e.text)
    return { text: e.text }
  })
  // the chat's own /model and /effort, and the model they leave the session on
  on('session.model', () => ({ value: model }))
  on('command.run', (_, e) => {
    ran.push([e.command, e.args])
    order.push(`/${e.command} ${e.args}`)
    if (e.command === 'model') model = `claude-${e.args}-5-5`
    if (e.command === 'effort') return { text: effortText(e.args) }
    return { text: e.command === 'model' ? `Set model to ${e.args}` : `Ran /${e.command}` }
  })
  // the chat's slash commands, as the typeahead lists them
  on('command.list', () => ({
    value: ['model', 'effort', 'rename', 'reload-plugins'].map(name => ({ name, description: '', source: 'builtin' })) as never,
  }))
  on('turn.step', async function* (_, e) {
    return { turnId: e.turnId, index: e.index, answer: '', toolUses: [], stopReason: 'end_turn' as const, usage: null }
  })
  return { files, submitted, aborted, ran, order, clock }
}

const reply = (body: unknown) => ({ value: { status: 200, ok: true, headers: {}, text: JSON.stringify(body) } })
const readChat = (files: Map<string, string>) => JSON.parse(files.get(`${RELAY}/chats/${SESSION}.json`) ?? '{}')
const readHistory = (files: Map<string, string>) =>
  JSON.parse(files.get(`${RELAY}/history/${SESSION}.json`) ?? '{}').messages as Record<string, unknown>[]
const START = { cwd: '/test/Documents/app/demo', surface: 'vscode', isInteractive: true } as const
const PNG = { type: 'base64', media_type: 'image/png', data: 'iVBORw0KGgo=' }

// a turn of tool calls as the transcript holds it: one done, one failed, one still running
const WORKED = [
  { role: 'user', text: 'fix the header', toolUses: [] },
  {
    role: 'assistant',
    text: 'Looking at it.',
    toolUses: [
      { tool_use_id: 'toolu_bash', tool: 'Bash', input: { command: 'npm test\n--watch' }, text: 'all 12 pass' },
      { tool_use_id: 'toolu_grep', tool: 'Grep', input: { pattern: 'Header' }, text: 'grep: bad flag', isError: true },
      { tool_use_id: 'toolu_read', tool: 'Read', input: { file_path: '/test/shots/header.png' } },
    ],
  },
]

test('the history holds a row per tool call, with its target, its state and its output', async ($, on) => {
  const { files } = world(on, 1_000_000, WORKED)
  await $.session.start(START)

  const items = readHistory(files)
  expect(items.map(item => item.role)).toEqual(['user', 'assistant', 'tool', 'tool', 'tool'])
  expect(items[2]).toEqual(
    expect.objectContaining({ tool: 'Bash', target: 'npm test', detail: 'npm test\n--watch', state: 'done', output: 'all 12 pass' }),
  )
  expect(items[3]).toEqual(expect.objectContaining({ tool: 'Grep', target: 'Header', state: 'error' }))
  expect(items[4]).toEqual(expect.objectContaining({ tool: 'Read', target: 'header.png', state: 'running' }))
})

test('the window holds the newest stretch: 60 messages, or 120 tool rows, whichever comes first', async ($, on) => {
  const long = Array.from({ length: 80 }, (_, n) => ({ role: 'assistant', text: `answer ${n}`, toolUses: [] }))
  const busy = Array.from({ length: 200 }, (_, n) => ({
    tool_use_id: `toolu_${n}`,
    tool: 'Read',
    input: { file_path: `/x/${n}.js` },
    text: 'ok',
  }))
  const talk = world(on, 1_000_000, long)
  await $.session.start(START)
  const items = readHistory(talk.files)
  expect(items.length).toBe(60)
  expect(items[0]?.text).toBe('answer 20')

  long.splice(0, long.length, { role: 'assistant', text: 'before the tools', toolUses: [] })
  long.push({ role: 'assistant', text: 'working', toolUses: busy } as never)
  await $.turn.complete({ answer: 'done', durationMs: 5, isAborted: false, turnId: 'turn-1', reason: 'answer' })
  const tools = readHistory(talk.files)
  expect(tools.filter(item => item.role === 'tool').length).toBe(120)
  expect(tools[0]?.id).toBe('toolu_80')
})

test('a picture a tool hands the model is saved, and its row names it', async ($, on) => {
  const { files, clock } = world(on, 1_000_000, WORKED)
  await $.session.start(START)

  await $.session.append({
    message: {
      type: 'user',
      role: 'user',
      content: [{ type: 'tool_result', tool_use_id: 'toolu_read', content: [{ type: 'image', source: PNG }] }],
    },
    door: 'tool-result',
    origin: { kind: 'tool', tool: 'Read' },
    uuid: 'row-0001',
  })
  await clock.advance(2_000)

  expect(JSON.parse(files.get(`${RELAY}/images/${SESSION}/toolu_read-0.json`) ?? '{}')).toEqual({
    mediaType: 'image/png',
    data: 'iVBORw0KGgo=',
  })
  expect(readHistory(files)[4]).toEqual(expect.objectContaining({ id: 'toolu_read', images: ['toolu_read-0'] }))
})

test('a picture pasted into a prompt goes to that prompt', async ($, on) => {
  const rows = [{ role: 'user', text: 'what about this one?', toolUses: [] }]
  const { files, clock } = world(on, 1_000_000, rows)
  await $.session.start(START)

  await $.session.append({
    message: {
      type: 'user',
      role: 'user',
      content: [
        { type: 'text', text: 'what about this one?' },
        { type: 'image', source: PNG },
      ],
    },
    door: 'prompt',
    origin: { kind: 'user' },
    uuid: 'row-0002',
  } as never)
  await clock.advance(2_000)

  expect(readHistory(files)[0]).toEqual({ role: 'user', text: 'what about this one?', images: ['row-0002-0'] })
})

test('a chat registers, and a phone message enters once as typed words', async ($, on) => {
  const { files, submitted, clock } = world(on, 1_000_000)
  await $.session.start({ cwd: '/test/Documents/app/demo', surface: 'vscode', isInteractive: true })

  const chat = JSON.parse(files.get(`${RELAY}/chats/${SESSION}.json`) ?? '{}')
  expect(chat.project).toBe('demo')
  expect(chat.began).toBe('can we build a phone relay?')
  expect(chat.status).toBe('idle')

  files.set(`${RELAY}/inbox/${SESSION}.json`, JSON.stringify({ messages: [{ id: '0001', text: 'from the phone', at: 1 }] }))
  await clock.advance(2_000)
  await clock.advance(2_000)

  expect(submitted.length).toBe(1)
  expect(submitted[0]?.text).toBe('from the phone')
  expect(submitted[0]?.origin).toEqual(expect.objectContaining({ kind: 'plugin', asUser: true }))
  expect(files.get(`${RELAY}/inbox/${SESSION}.ack`)).toBe('0001')
})

test('a phone message counts as entered at the latest as the turn it started ends', async ($, on) => {
  const { files, clock } = world(on, 1_000_000)
  await $.session.start(START)

  files.set(`${RELAY}/inbox/${SESSION}.json`, JSON.stringify({ messages: [{ id: '0001', text: 'from the phone', at: 1 }] }))
  await clock.advance(2_000)
  expect(files.get(`${RELAY}/inbox/${SESSION}.entered`)).toBeUndefined()

  await $.turn.start({ text: 'from the phone', turnId: 'turn-1' })
  await $.turn.complete({ answer: 'done', durationMs: 5, isAborted: false, turnId: 'turn-1', reason: 'answer' })
  expect(files.get(`${RELAY}/inbox/${SESSION}.entered`)).toBe('0001')
})

test('a phone message counts as entered as soon as the history shows it, while its turn runs', async ($, on) => {
  const rows = [...OPENING]
  const { files, clock } = world(on, 1_000_000, rows)
  await $.session.start(START)

  files.set(`${RELAY}/inbox/${SESSION}.json`, JSON.stringify({ messages: [{ id: '0001', text: 'from the phone', at: 1 }] }))
  await clock.advance(2_000)
  expect(files.get(`${RELAY}/inbox/${SESSION}.entered`)).toBeUndefined()

  // the turn starts on the message, and the history rewritten a moment in shows it: one copy on the phone
  rows.push({ role: 'user', text: 'from the phone', toolUses: [] })
  await $.turn.start({ text: 'from the phone', turnId: 'turn-1' })
  await clock.advance(1_500)
  expect(readHistory(files).at(-1)).toEqual({ role: 'user', text: 'from the phone' })
  expect(files.get(`${RELAY}/inbox/${SESSION}.entered`)).toBe('0001')
})

test('a Stop from the phone ends the running turn, once, and leaves the model a line after it', async ($, on) => {
  const { files, aborted, clock } = world(on, 1_000_000)
  const session = mock.session(on)
  await $.session.start(START)
  await $.turn.start({ text: 'build the stop button', turnId: 'turn-1' })

  files.set(`${RELAY}/inbox/${SESSION}.stop`, JSON.stringify({ at: 1_000_000 }))
  await clock.advance(2_000)
  await clock.advance(2_000)
  expect(aborted).toEqual(['turn-1'])
  expect(files.get(`${RELAY}/inbox/${SESSION}.stop`)).toBe('')

  await $.turn.complete({ answer: '', durationMs: 5, isAborted: true, turnId: 'turn-1', reason: 'aborted' })
  const rows = session.appended()
  expect(rows.length).toBe(1)
  expect(rows[0]?.message.type).toBe('user')
  expect(JSON.stringify(rows[0]?.message.content)).toContain('stopped the turn above from the phone')
  expect(readChat(files).status).toBe('idle')
})

test('a Stop acts on a running turn alone, within half a minute', async ($, on) => {
  const { files, aborted, clock } = world(on, 1_000_000)
  await $.session.start(START)

  files.set(`${RELAY}/inbox/${SESSION}.stop`, JSON.stringify({ at: 1_000_000 }))
  await clock.advance(2_000)
  await $.turn.start({ text: 'a later turn', turnId: 'turn-2' })
  files.set(`${RELAY}/inbox/${SESSION}.stop`, JSON.stringify({ at: 900_000 }))
  await clock.advance(2_000)

  expect(aborted).toEqual([])
})

test("a switch from the phone runs the chat's own /model and /effort once, and the chat file says what it runs on", async ($, on) => {
  const { files, ran, clock } = world(on, 1_000_000)
  await $.session.start(START)
  expect(readChat(files).model).toBe('claude-opus-5-5')

  files.set(`${RELAY}/inbox/${SESSION}.switch`, JSON.stringify({ model: 'sonnet', effort: 'max', at: 999_000 }))
  await clock.advance(2_000)
  await clock.advance(2_000)

  expect(ran).toEqual([
    ['model', 'sonnet'],
    ['effort', 'max'],
  ])
  expect(files.get(`${RELAY}/inbox/${SESSION}.switch`)).toBe('')
  expect(readChat(files)).toEqual(expect.objectContaining({ model: 'claude-sonnet-5-5', effort: 'max', switchedAt: 999_000 }))
})

test('an effort past the cap reports the level the chat took, and a refused one leaves the level as it was', async ($, on) => {
  const capped = (level: string) =>
    level === 'max'
      ? "Effort 'max' exceeds the cap for claude-opus-5-5 set by your settings or organization; set to 'xhigh' instead"
      : `Not applied: CLAUDE_CODE_EFFORT_LEVEL=low overrides effort this session, and ${level} is session-only`
  const { files, ran, clock } = world(on, 1_000_000, OPENING, capped)
  await $.session.start(START)

  files.set(`${RELAY}/inbox/${SESSION}.switch`, JSON.stringify({ effort: 'max', at: 999_000 }))
  await clock.advance(2_000)
  await clock.advance(2_000)
  expect(readChat(files)).toEqual(expect.objectContaining({ effort: 'xhigh', switchedAt: 999_000 }))

  files.set(`${RELAY}/inbox/${SESSION}.switch`, JSON.stringify({ effort: 'medium', at: 999_500 }))
  await clock.advance(2_000)
  await clock.advance(2_000)
  expect(ran.map(([command]) => command)).toEqual(['effort', 'effort'])
  expect(readChat(files)).toEqual(expect.objectContaining({ effort: 'xhigh', switchedAt: 999_500 }))
})

test("a rename from the phone runs the chat's own /rename once, the name on one line", async ($, on) => {
  const { files, ran, clock } = world(on, 1_000_000)
  await $.session.start(START)

  files.set(`${RELAY}/inbox/${SESSION}.rename`, JSON.stringify({ title: '  Wallets\nmenu  ', at: 999_000 }))
  await clock.advance(2_000)
  await clock.advance(2_000)

  expect(ran).toEqual([['rename', 'Wallets menu']])
  expect(files.get(`${RELAY}/inbox/${SESSION}.rename`)).toBe('')
})

test("a slash command from the phone runs as the chat's own, its line and output in the history", async ($, on) => {
  const rows = [...OPENING]
  const { files, ran, submitted, clock } = world(on, 1_000_000, rows)
  await $.session.start(START)

  files.set(`${RELAY}/inbox/${SESSION}.json`, JSON.stringify({ messages: [{ id: '0001', text: '/reload-plugins', at: 1 }] }))
  // the transcript's row for the command, as Claude Code records one
  rows.push({ role: 'user', text: '<command-name>/reload-plugins</command-name> <command-message>reload-plugins</command-message> <command-args></command-args>', toolUses: [] })
  await clock.advance(2_000)
  await clock.advance(2_000)

  expect(ran).toEqual([['reload-plugins', '']])
  expect(submitted).toEqual([])
  expect(files.get(`${RELAY}/inbox/${SESSION}.entered`)).toBe('0001')
  expect(readHistory(files).at(-1)).toEqual({ role: 'command', text: '/reload-plugins', output: 'Ran /reload-plugins' })
})

test('a message opening with a slash that names no command goes as words, behind a zero-width space', async ($, on) => {
  const { files, ran, submitted, clock } = world(on, 1_000_000)
  await $.session.start(START)

  files.set(`${RELAY}/inbox/${SESSION}.json`, JSON.stringify({ messages: [{ id: '0001', text: '/test/shots/a.png look at this', at: 1 }] }))
  await clock.advance(2_000)
  await clock.advance(2_000)

  expect(ran).toEqual([])
  expect(submitted.map(s => s.text)).toEqual(['\u200b/test/shots/a.png look at this'])
})

test('a slash command keeps its place among the words around it', async ($, on) => {
  const { files, order, clock } = world(on, 1_000_000)
  await $.session.start(START)

  files.set(
    `${RELAY}/inbox/${SESSION}.json`,
    JSON.stringify({
      messages: [
        { id: '0001', text: 'first words', at: 1 },
        { id: '0002', text: '/model sonnet', at: 2 },
        { id: '0003', text: 'last words', at: 3 },
      ],
    }),
  )
  for (let i = 0; i < 4; i += 1) await clock.advance(2_000)

  expect(order).toEqual(['first words', '/model sonnet', 'last words'])
})

test("the effort the main loop's requests go out at shows in the chat file; a subagent's leaves it", async ($, on) => {
  const { files } = world(on, 1_000_000)
  await $.session.start(START)
  await $.turn.start({ text: 'go', turnId: 'turn-1' })

  const read = async (stream: AsyncGenerator<unknown, unknown> & { result: Promise<unknown> }) => {
    for await (const _ of stream) {
      // the response's pieces, read to the end
    }
    return stream.result
  }
  await read($.turn.step({ turnId: 'turn-1', index: 0, model: 'claude-opus-5-5', effort: 'medium', messageCount: 2 }))
  expect(readChat(files).effort).toBe('medium')

  await read($.turn.step({ turnId: 'turn-1', index: 1, model: 'claude-haiku-5-5', effort: 'low', messageCount: 3, agentId: 'a1' }))
  expect(readChat(files).effort).toBe('medium')
})

test('the main loop names its running tool in the chat file while it runs', async ($, on) => {
  const { files } = world(on, 1_000_000)
  let during: unknown
  on('tool.call', () => {
    during = readChat(files).tools
    return { result: {} }
  })
  await $.session.start(START)

  await $.tool.call({ tool: 'Bash', command: 'npm test' })

  expect(during).toEqual(['Bash'])
  expect(readChat(files).tools).toEqual([])
})

test('a chat that loads the mod mid-session registers at its next turn, under the folder it started in', async ($, on) => {
  const { files } = world(on, 1_000_000)
  // the shell has moved into a subfolder since the chat began; the transcript stays under the root
  on('session.cwd', () => ({ value: '/test/Documents/app/later/tmp/post' }))
  on('session.root', () => ({ value: '/test/Documents/app/later' }))

  await $.turn.start({ text: 'typed after the reload', turnId: 'turn-1' })

  const chat = JSON.parse(files.get(`${RELAY}/chats/${SESSION}.json`) ?? '{}')
  expect(chat.cwd).toBe('/test/Documents/app/later')
  expect(chat.project).toBe('later')
  expect(chat.status).toBe('working')
})

// the dialog beneath stays open until the test answers it, the way the panel's waits on the person
function dialog(on: On) {
  const opened: { answer: (result: unknown) => void }[] = []
  on('tool.call', () => new Promise(resolve => opened.push({ answer: result => resolve({ result } as never) })))
  return opened
}

test('a question goes to the dialog and the phone at once, wherever the person is; the phone can answer first', async ($, on) => {
  const { files } = world(on, 1_000_000)
  let asked = ''
  on('http.fetch', (_, e) => {
    asked = e.url
    return reply({ state: 'answered', answer: { answers: { 'Which phone app?': 'Web app' } } })
  })
  const opened = dialog(on)
  await $.session.start(START)
  files.set(`${RELAY}/away.json`, JSON.stringify({ away: false, at: 1_000_000 }))

  const answered = await $.tool.call({ tool: 'AskUserQuestion', questions: [QUESTION] })

  expect(opened.length).toBe(1)
  expect(asked).toContain(`/relay/wait?session=${SESSION}`)
  expect(asked).toContain('always=1')
  expect((answered.result as { answers?: unknown } | undefined)?.answers).toEqual({ 'Which phone app?': 'Web app' })
  expect(files.get(`${RELAY}/asks/${SESSION}.json`)).toBe('')
})

test('the dialog can answer first, and the phone lets the question go', async ($, on) => {
  const { files } = world(on, 1_000_000)
  on('http.fetch', () => reply({ state: 'waiting' }))
  on('tool.call', () => ({ result: { questions: [QUESTION], answers: { 'Which phone app?': 'Native' } } }))
  await $.session.start(START)

  const answered = await $.tool.call({ tool: 'AskUserQuestion', questions: [QUESTION] })

  expect((answered.result as { answers?: unknown } | undefined)?.answers).toEqual({ 'Which phone app?': 'Native' })
  expect(files.get(`${RELAY}/asks/${SESSION}.json`)).toBe('')
})

test('a Stop while a question waits ends the turn and lets the question go', async ($, on) => {
  const { files, aborted } = world(on, 1_000_000)
  on('http.fetch', () => reply({ state: 'stopped' }))
  dialog(on)
  await $.session.start(START)
  await $.turn.start({ text: 'ask me something', turnId: 'turn-3' })

  const answered = await $.tool.call({ tool: 'AskUserQuestion', questions: [QUESTION] })

  expect(aborted).toEqual(['turn-3'])
  expect(answered).toEqual(expect.objectContaining({ deny: 'The turn was stopped.' }))
  expect(files.get(`${RELAY}/asks/${SESSION}.json`)).toBe('')
})

test('away from the Mac, a permission takes the phone allow', async ($, on) => {
  const { files } = world(on, 1_000_000)
  on('http.fetch', () => reply({ state: 'answered', answer: { allow: true } }))
  await $.session.start({ cwd: '/test/Documents/app/demo', surface: 'vscode', isInteractive: true })
  files.set(`${RELAY}/away.json`, JSON.stringify({ away: true, at: 1_000_000 }))

  const decided = await $.classic.PermissionRequest({ tool_name: 'Bash', tool_input: { command: 'ls -la' } })

  expect(decided.decision).toEqual({ behavior: 'allow' })
  const ask = files.get(`${RELAY}/asks/${SESSION}.json`)
  expect(ask).toBe('')
})
