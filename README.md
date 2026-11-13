# Uselim

Claude's usage limits in the Mac's menu bar, and the Claude Code chats open in VS Code, followed and answered from an
iPhone.

The full guide, with pictures: https://simonbooban.com/resources/how-to-use-uselim

## What it does

- **Limits in the menu bar.** The session and weekly limits as Claude's usage panel shows them, each with its reset
  time, orange from 60% and red from 80%, with a notice at 60, 80 and 100% and when a limit resets. On a Mac with a
  Touch Bar, the menu's Touch Bar switch puts the same figures on the strip.
- **Chats waiting on an answer.** A chat that stops for a question or a permission shows at the top of the menu, with
  an orange dot on the bar.
- **The phone (optional, off until turned on).** A Home Screen app on the iPhone, reached through Tailscale:
  - every chat open in VS Code, its replies, tool calls and code drawn as VS Code draws them
  - messages, photos and files sent to a chat as typed prompts, and a new chat opened on the Mac
  - questions and permissions answered from the phone, beside VS Code's own dialog, the first answer standing
  - each chat's model and effort switched, chats renamed and hidden, slash commands run
  - notifications for replies, questions, permissions and limits

## Install

**Through Culpmit:** turn on Claude limits in Culpmit's menu. Culpmit downloads Uselim, checks its signature and sets
it up in the background.

**By hand:**

1. Download `Uselim-<version>.zip` from this repo's Releases and move `Uselim.app` to Applications.
2. In Terminal: `/Applications/Uselim.app/Contents/MacOS/Uselim --setup`

Uselim needs Claude Code, signed in, either as the `claude` command or inside VS Code's Claude Code extension, and an
Intel or Apple silicon Mac on macOS 13 or later.

## What it changes on the Mac

- **Claude Code's settings** (`~/.claude/settings.json`): six hooks, so Uselim hears questions, permission asks and
  replies, and a status line that hands Uselim the limits after each reply, set only where no status line is set. The
  file as it stood is copied to `~/Library/Application Support/Uselim/backups` before each change.
- **Claude Code's plugin `uselim`**, installed from the copy inside the app; it carries the phone's messages into each
  chat.
- **Open at login.**
- **The Claude Code login in the keychain**, read to ask Anthropic for the usage figures, the same figures Claude's
  usage panel shows. Everything else stays on the Mac: the phone reaches it through Tailscale, and Uselim asks this
  repo's Releases for its own updates.

## The phone

1. On the iPhone, install Tailscale and sign in; the free Personal plan covers it.
2. In Uselim's menu, turn on **Phone**. The line beneath it takes the Mac through Tailscale a click at a time: its
   installer when the Mac needs one, signing in with the iPhone's account, then Serve and HTTPS for Uselim's relay.
3. Choose **Pair a phone…**: a QR code and six digits, with a button that copies them. The digits pair one phone
   within ten minutes, and fresh ones follow while the window stays open.
4. On the iPhone, scan the QR code with the Camera to open Uselim in Safari, then tap Share, then Add to Home Screen.
5. Open Uselim from the Home Screen, tap the scan icon in its code field and point it at Pair a phone…, or type the
   six digits; then turn notifications on in its Notification Center screen.

Pairing gives each phone its own key, which the Mac keeps as a hash beside the phone's name; Pair a phone… lists the
phones, each with Remove, which ends its key and its notifications. Every request carries a key: the phone its own,
and Uselim's own parts on the Mac a key only your account can read, so other apps on the Mac reach the page's files
alone. Requests from the phone carry the Mac's own name on the tailnet, and Tailscale's login, where it names one, is
the Mac's. A device that sends five wrong keys waits a minute before its next one, doubling up to an hour, and devices
yet to pair share one such count. A request's checks run before any of its body is read. Notifications go through
Apple's push service alone, to the paired phones.

## What Uselim asks for, and why

Each ask comes once: what it serves, and what carries on when you decline it.

### On the Mac

- **Notifications**, with Uselim's first notice. They carry a limit crossing 60, 80 and 100%, the reset of a limit
  that warned, and a chat stopping to ask you something. Declined, the menu still shows every limit and every waiting
  chat, and offers Turn on notifications…, which opens the setting to allow them.
- **Open at login**, a note from macOS that a login item was added. Uselim starts with the Mac, so the readout is on
  the bar from the start. To start Uselim yourself instead, turn Open at login off in its menu.
- **VS Code's question before a link opens a chat**, the first time. Uselim opens chats through such a link for the
  phone's New chat, for a message to a closed chat, and to bring forward a chat whose phone message waits to go in.
  Tick the box to skip the question from then on. Declined, a message to a closed chat waits until you open that chat,
  and New chat keeps your first message on the phone.
- **The hooks and the plugin**, should Claude Code ask about them at the next chat start. The hooks tell Uselim which
  chat waits on you and carry permission asks to the phone; the plugin carries the phone's messages into each chat,
  and its replies and questions back. The limits in the menu bar work either way.
- **Tailscale**, with Phone on: its installer asks for your admin password, macOS for approval of Tailscale's VPN
  configuration, and Tailscale for a sign-in. Tailscale carries the iPhone to the Mac over your own tailnet. Declined,
  the phone side waits, and the limits and the menu bar work as before.
- **Claude's usage figures** arrive with zero prompts.

### On the iPhone

- **Tailscale's VPN configuration**, when you first turn Tailscale on. The phone reaches the Mac's relay through it
  alone.
- **The camera**, once, for the scan icon on the pairing screen, to read the QR code in the Mac's Pair a phone…
  window. Declined, the six digits typed in pair the phone the same way.
- **Notifications**, when you turn them on in the app's Notification Center screen. Replies, questions, permissions
  and limits arrive as notices, each kind with its own switch. Declined, every notice still lists under the bell, with
  a dot until you open it.

## Remove

Choose **Remove Uselim…** in its menu, or run `/Applications/Uselim.app/Contents/MacOS/Uselim --remove`. Either one
takes the hooks and status line out of Claude Code's settings (a backup kept), removes the plugin and Open at login,
and moves the app to the Trash.

## Build from source

`./build.sh` builds `build.noindex/Uselim.app` with the Swift compiler from Xcode's command line tools;
`./build.sh install` puts it in Applications and opens it. The phone's web app lives in `Resources/Web`, the Claude
Code plugin in `mod/` (its tests: `claude plugin test mod`).

## License

MIT, see [LICENSE](LICENSE). The app icon's glyph comes from Tabler Icons (MIT); the phone's code colours come from
highlight.js (BSD-3-Clause).
