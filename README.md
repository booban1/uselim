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
3. Choose **Pair a phone…**: a QR code and a six-digit code, with a button that copies the code.
4. On the iPhone, scan the QR code with the Camera to open Uselim in Safari, then tap Share, then Add to Home Screen.
5. Open Uselim from the Home Screen, tap the scan icon in its code field and point it at Pair a phone…, or type the
   six digits; then turn notifications on in its Notification Center screen.

Every request from the phone carries the pairing code; the relay answers the Mac itself and paired phones alone. A
device that sends five wrong codes waits a minute before its next one, doubling up to an hour.

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
