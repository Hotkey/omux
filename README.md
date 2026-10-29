# omux

A native macOS terminal for running many [Claude Code](https://claude.com/claude-code) sessions side by side. Sessions are grouped into projects in a sidebar, each one keeps its own Claude conversation across relaunches, and a files panel previews whatever the session is working on.

![omux with a worktree session running three subagents, a session waiting on Allow or Deny, and the Changes tab showing the diff with a review note](docs/screenshot.png)

Built with SwiftUI and [SwiftTerm](https://github.com/migueldeicaza/SwiftTerm). A Windows version with the same features lives in [`windows/`](#windows).

## Features

- **Projects and sessions.** A project is a folder plus a default command; sessions live under it in the sidebar. Drag to reorder or move sessions between projects, rename them, and give them colors.
- **Conversations that resume.** Each session that runs `claude` is pinned to its own session id, so quitting and reopening omux continues the same conversation.
- **Live status.** Each session shows whether Claude is working, needs you or is done, and the Dock badge counts sessions waiting on you.
- **Notifications.** When a session you aren't looking at needs you, omux posts a macOS notification; clicking it opens the session. Notifications for finished sessions are optional. Programs can send their own with OSC 9 or OSC 777: set Claude Code's notification channel to `ghostty` (in `/config`) to get its notifications, and its plugins', this way. Turn each kind on or off in Settings › Notifications.
- **Allow or deny from the sidebar.** When Claude asks for a tool permission, Allow and Deny appear under the session and in its header, so you can answer without switching to it.
- **Subagents.** While Claude runs subagents, the selected session lists them in the sidebar with what each one is doing; other sessions show a count. The header's agents button lists them all, and clicking one opens its transcript in the right panel.
- **Worktree sessions** (⌥⌘T). A session can get its own Git branch and folder (`<repo>.worktrees/<branch>` next to the repo), so parallel sessions don't touch each other's files. Turn it on per project to make it the default. Right-click a worktree session to create a pull request, merge it into its base branch or open its folder; closing one asks whether to keep or delete the worktree.
- **Pull request status.** With the [GitHub CLI](https://cli.github.com) installed, a worktree session whose branch has a pull request shows its number and state in the sidebar and header: checks running or failing, changes requested, approved or merged. Click the header chip to see each check and open it on GitHub. The session asks for your attention when checks start failing, a review comes in or the pull request is merged. Once it's merged, the session offers to delete its worktree and branch.
- **Right panel** (⌥⌘F), with three tabs:
  - **Files** (⌥⌘1). Browses the session's folder and previews Markdown, code and text (with optional line wrapping), images, PDFs, audio, video, and anything else Quick Look can show. File paths mentioned in the session's output are listed at the top; ⌘-click a path in the terminal to preview it.
  - **Changes** (⌥⌘2). The session's diff: against the base branch for worktree sessions, uncommitted changes otherwise. Click a line to leave a note, then send all notes to Claude in one message.
  - **Browser** (⌥⌘3). A web view per session that opens the local server the session prints (like `localhost:3000`). The pick button sends an element's HTML, styles and a screenshot to the prompt.
- **Recent conversations** (⇧⌘O). Reopen any Claude Code conversation found in `~/.claude/projects`.
- **Usage limits.** The sidebar shows your Claude plan's 5-hour and weekly limits (see [Privacy](#privacy)).
- **Open folders from anywhere.** Finder's right-click "Open in omux" service, dropping a folder on the Dock icon, or `open -a omux <folder>`.
- Themes, terminal font and size, and a quick switcher (⌘K).

## Requirements

- macOS 14 or later
- Swift 5.9 or later (Xcode 15+ or the Command Line Tools)
- [Claude Code](https://claude.com/claude-code) installed as `claude` for Claude sessions; plain shell sessions work without it

## Build

```sh
./build.sh
open /Applications/omux.app
```

`build.sh` builds a release binary, assembles `build/omux.app`, and then **replaces `/Applications/omux.app`** so the Finder service and Dock entry stay registered. It signs with the first "Apple Development" certificate in your keychain, or ad hoc if there is none.

## Keyboard shortcuts

| Action | Shortcut |
| --- | --- |
| New session / new shell / new project | ⌘T / ⇧⌘T / ⇧⌘N |
| New session in a worktree | ⌥⌘T |
| Open folder / recent Claude conversation | ⌘O / ⇧⌘O |
| Close session | ⌘W |
| Rename / edit session | ⌘R / ⌘I |
| Restart session | ⇧⌘R |
| Interrupt (Ctrl-C) | ⇧⌘. |
| Continue Claude conversation | ⇧⌘↩ |
| Jump to session | ⌘K |
| Next / previous session | ⇧⌘] / ⇧⌘[ |
| Session 1–9 in the current project | ⌘1 … ⌘9 |
| Right panel / Files / Changes / Browser | ⌥⌘F / ⌥⌘1 / ⌥⌘2 / ⌥⌘3 |

## Privacy

omux has no telemetry and makes two kinds of network request:

- **Usage limits.** To show your plan limits, omux reads Claude Code's login from the macOS Keychain (the `Claude Code-credentials` item, via `/usr/bin/security`) and sends it to `https://api.anthropic.com/api/oauth/usage`, the same endpoint Claude Code's `/usage` command uses. The token is never written to disk or logged. This endpoint is undocumented and may change or stop working at any time.
- **Pull request status.** If `gh` is installed, omux runs `gh pr view` for each worktree session's branch about once a minute and when you switch back to omux. gh uses its own login; omux never reads or stores your GitHub token.

Local files omux touches:

- `~/Library/Application Support/omux/workspace.json`: your projects and sessions (plus a `.bak` copy).
- `~/.claude/projects`: read to list recent conversations, to check whether a session's conversation exists, and to show a running session's subagents and their transcripts.
- Worktree sessions run `git` in your repository, and creating a pull request runs `git push` through your login shell. Nothing else is pushed or committed unless you confirm it.
- Elements picked in the browser are saved under your temporary folder (`omux-picks`) so Claude can read them.
- Settings are stored in the standard macOS preferences.

## Windows

`windows/` is a native Windows port with the same features: projects and sessions, conversations that resume, live status, notifications, Allow and Deny from the sidebar, subagents, worktree sessions with pull request status, the right panel's Files, Changes and Browser tabs, recent conversations, usage limits, themes and the quick switcher. It is a WPF app; each session runs in a Windows pseudo console (ConPTY) and is drawn by [xterm.js](https://xtermjs.org) in WebView2.

### Requirements

- Windows 10 1809 or later (Windows 11 recommended)
- The Microsoft Edge WebView2 Runtime (preinstalled on Windows 11 and current Windows 10)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build; the built app is self-contained
- [Claude Code](https://claude.com/claude-code) on your `PATH` as `claude` for Claude sessions

### Build

```powershell
windows\build.ps1
windows\dist\omux\omux.exe
```

`build.ps1` publishes a self-contained `omux.exe` to `windows\dist\omux` and zips it. For development, `dotnet run --project windows` works too.

Sessions run in PowerShell 7 when it is installed, otherwise Windows PowerShell; Settings can switch to `cmd.exe`, Git Bash or any other shell. Settings also adds "Open in omux" to Explorer's right-click menu on folders. You can also drop a folder on the window or run `omux.exe <folder>`, which hands the folder to the running omux.

### Keyboard shortcuts

Ctrl+T, Ctrl+R, Ctrl+K and Ctrl+O belong to Claude Code and the shell, so omux's own shortcuts use Ctrl+Shift. The ••• button in the sidebar lists every command.

| Action | Shortcut |
| --- | --- |
| New session / new shell / new project | Ctrl+Shift+T / Ctrl+Shift+Alt+T / Ctrl+Shift+N |
| New session in a worktree | Ctrl+Shift+G |
| Open folder / recent Claude conversation | Ctrl+Shift+O / Ctrl+Shift+Alt+O |
| Close session | Ctrl+Shift+W |
| Rename / edit session | F2 / Ctrl+Shift+I |
| Restart session | Ctrl+Shift+R |
| Interrupt (Ctrl-C) | Ctrl+Shift+. |
| Continue Claude conversation | Ctrl+Shift+Enter |
| Jump to session | Ctrl+Shift+K |
| Next / previous session | Ctrl+Tab / Ctrl+Shift+Tab |
| Session 1–9 in the current project | Ctrl+1 … Ctrl+9 |
| Right panel / Files / Changes / Browser | Ctrl+Shift+E / Ctrl+Shift+1 / Ctrl+Shift+2 / Ctrl+Shift+3 |
| Find in session (Enter / Shift+Enter to step, Esc to close) | Ctrl+Shift+F |
| Bigger / smaller / default text | Ctrl+= or Ctrl+Num + / Ctrl+- or Ctrl+Num - / Ctrl+0 |
| Settings / quit | Ctrl+, / Alt+F4 |

In a terminal, Ctrl+C copies when text is selected, Ctrl+V pastes, right-click copies the selection or pastes, and Ctrl-click opens a URL or previews a file path.

When a session in the background rings the bell or waits for you, the taskbar button flashes and shows how many sessions are waiting. Notifications appear as Windows notifications; clicking one opens its session.

Worktrees go next to the repo in `<repo>.worktrees\<branch>`. Creating a pull request runs `git push` with your usual Git credentials (Git Credential Manager may ask you to sign in), and pull request status needs the [GitHub CLI](https://cli.github.com) (`gh`) signed in.

### Privacy on Windows

The same network requests as on macOS: usage limits, and pull request status through `gh` when it is installed. Claude Code on Windows keeps its login in `%USERPROFILE%\.claude\.credentials.json`; omux reads the access token from there and never writes or logs it. Your projects and sessions are saved to `%APPDATA%\omux\workspace.json` (plus a `.bak` copy) and settings to `%APPDATA%\omux\settings.json`. Elements picked in the browser are saved under `%TEMP%\omux-picks`.

## Disclaimer

omux is an independent project and is not affiliated with or endorsed by Anthropic. Claude and Claude Code are trademarks of Anthropic.

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for what changed in each version.

## License

[MIT](LICENSE)
