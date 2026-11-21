# Uselim

Claude's usage limits in the Mac's menu bar, on the Windows taskbar and in Linux's tray, and the Claude Code chats
open in VS Code, followed and answered from a phone, iPhone or Android.

The full guide, with pictures: https://simonbooban.com/resources/how-to-use-uselim

## What it does

- **Limits in the menu bar.** The session and weekly limits as Claude's usage panel shows them, each with its reset
  time, orange from 60% and red from 80%, with a notice at 60, 80 and 100% and when a limit resets. On a Mac with a
  Touch Bar, the menu's Touch Bar switch puts the same figures on the strip.
- **Chats waiting on an answer.** A chat that stops for a question or a permission shows at the top of the menu, with
  an orange dot on the bar.
- **The phone (optional, off until turned on).** A Home Screen app on the phone, iPhone or Android, reached through
  Tailscale:
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

1. On the phone, install Tailscale and sign in; the free Personal plan covers it.
2. In Uselim's menu, turn on **Phone**. The line beneath it takes the Mac through Tailscale a click at a time: its
   installer when the Mac needs one, signing in with the phone's account, then Serve and HTTPS for Uselim's relay.
3. Choose **Pair a phone…**: a QR code and six digits, with a button that copies them. The digits pair one phone
   within ten minutes, and fresh ones follow while the window stays open.
4. On the phone, scan the QR code with the Camera to open Uselim: on an iPhone in Safari, then tap Share, then Add to
   Home Screen; on Android in Chrome, then ⋮, then Add to Home screen.
5. Open Uselim from the Home Screen, tap the scan icon in its code field and point it at Pair a phone…, or type the
   six digits; then turn notifications on in its Notification Center screen.

Pairing gives each phone its own key, which the Mac keeps as a hash beside the phone's name; Pair a phone… lists the
phones, each with Remove, which ends its key and its notifications. Every request carries a key: the phone its own,
and Uselim's own parts on the Mac a key only your account can read, so other apps on the Mac reach the page's files
alone. Requests from the phone carry the Mac's own name on the tailnet, and Tailscale's login, where it names one, is
the Mac's. A device that sends five wrong keys waits a minute before its next one, doubling up to an hour, and devices
yet to pair share one such count. A request's checks run before any of its body is read. Notifications go through the
phone's own push service (Apple's for an iPhone, Google's for Chrome on Android), to the paired phones alone.

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
  configuration, and Tailscale for a sign-in. Tailscale carries the phone to the Mac over your own tailnet. Declined,
  the phone side waits, and the limits and the menu bar work as before.
- **Claude's usage figures** arrive with zero prompts.

### On the phone

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

## Windows

Uselim for Windows carries the limits, the waiting chats and the phone, iPhone or Android.

- **On the taskbar.** The same two lines as the Mac's menu bar, left of the notification area and anything Windows
  docks beside it, such as the weather: the session's share and
  its time left above, the week below, orange from 60% and red from 80%, an orange dot while a chat waits on an
  answer. A click opens the panel: each limit with its reset, the waiting chats, Open at login, Show on the taskbar,
  Remove Uselim. The tray keeps an asterisk icon with the same menu.
- **Notices** at 60, 80 and 100%, when a limit that warned resets, and when a chat stops to ask you something.
- **The phone**, as on the Mac: Phone in the panel takes the PC through Tailscale a click at a time (its installer,
  the sign-in, then Serve and HTTPS), and Pair a phone… shows the QR code and the six digits. The steps on the phone
  are the ones under The phone above.

**Install:** download `Uselim-<version>-windows.zip` from a `windows-v` release, open `Uselim.exe`, and choose OK in
Set up Uselim?. It needs Windows 10 or 11 and Claude Code, signed in. Windows 11 places a new tray icon behind the
arrow, so the first run opens Settings › Taskbar › Other system tray icons, where one switch pins Uselim.

**What it changes on the PC:**

- `%LOCALAPPDATA%\Programs\Uselim\Uselim.exe`, the app, in your own folders, so it asks for zero administrator rights.
- Claude Code's settings (`%USERPROFILE%\.claude\settings.json`): six hooks and, when that slot is free, the status
  line. The file as it stood is copied to `%LOCALAPPDATA%\Uselim\backups` before each change.
- Claude Code's plugin `uselim`, installed from the copy Uselim writes beside its exe; it carries the phone's messages
  into each chat.
- Open at login, as Uselim's entry under Windows' Run list for your account.
- An entry in Settings › Apps, under your own account, with Uselim's version and its Uninstall.
- The Claude Code login in `%USERPROFILE%\.claude\.credentials.json`, read to ask Anthropic for the usage figures.

**Remove:** Remove Uselim… in the panel, Uninstall in Settings › Apps, or
`%LOCALAPPDATA%\Programs\Uselim\Uselim.exe --remove`. Each one takes the hooks and status line out of Claude Code's
settings (a backup kept), removes the plugin, ends Open at login, takes Uselim out of Settings › Apps and deletes the
app.

**Build from source:** `windows/build.sh` builds `Uselim.exe` on a Mac with the .NET SDK (`./build.sh test` runs the
core's checks first); the Windows workflow in `.github/workflows/windows.yml` builds, checks and signs releases on
GitHub's Windows runner.

## Linux

Uselim for Linux carries the limits, the waiting chats and the phone, as on Windows.

- **In the tray.** The asterisk in the limits' tone with "9% · 79%" beside it on Cinnamon, Linux Mint's desktop; on
  other desktops the asterisk alone, the figures in its tooltip. A click opens the panel: each limit with its reset,
  the waiting chats, Phone and Pair a phone…, Open at login, Remove Uselim.
- **Notices** through the desktop's notifications at 60, 80 and 100%, and one per chat that stops to ask you
  something, closed once the chat moves on.

**Install:** download `Uselim-<version>-linux-x64.tar.gz`, or the `arm64` one, from a `linux-v` release, unpack it
and run `./uselim`. It sets itself up in `~/.local/bin`, joins the app menu, opens at login and starts in the tray. It
needs Claude Code, signed in, and a panel that shows tray icons: Cinnamon on Linux Mint, KDE Plasma, or Ubuntu's GNOME.

**What it changes on the computer:** `~/.local/bin/uselim`; Claude Code's settings (`~/.claude/settings.json`), with
the hooks and, when that slot is free, the status line, the file as it stood copied to `~/.local/share/Uselim/backups`
first; Claude Code's plugin `uselim`; the menu entry and its icon under `~/.local/share`; and Open at login as
`~/.config/autostart/uselim.desktop`.

**Remove:** Remove Uselim… in the panel, or `uselim --remove`, which takes each of those back.

**Privacy:** the same as on Windows, with Uselim's updates from its `linux-v` releases.

## Code signing policy

Free code signing on Windows provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

- Committers and reviewers: [booban1](https://github.com/booban1)
- Approvers: [booban1](https://github.com/booban1)

Every Windows release is built from this repository by its GitHub workflow, and each signing waits for an approver's
yes in SignPath.

**Privacy:** Uselim for Windows connects to `api.anthropic.com`, with the Claude Code login, for the usage figures
Claude's own usage panel shows; to GitHub (`api.github.com`, `github.com`) for Uselim's own updates; and, with Phone
on, to the paired phones' own push services (Apple's, Google's, Mozilla's) for their notifications. Tailscale carries
the phone to the PC over your own tailnet. Everything else stays on the computer.

## Build from source

`./build.sh` builds `build.noindex/Uselim.app` with the Swift compiler from Xcode's command line tools;
`./build.sh install` puts it in Applications and opens it. The phone's web app lives in `Resources/Web`, the Claude
Code plugin in `mod/` (its tests: `claude plugin test mod`).

## License

MIT, see [LICENSE](LICENSE). The app icon's glyph comes from Tabler Icons (MIT); the phone's code colours come from
highlight.js (BSD-3-Clause).
