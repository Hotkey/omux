# Changelog

All notable changes to omux are listed here. Versions follow [Semantic Versioning](https://semver.org).

## [0.3.0] - 2026-10-29

### Added

- **Pull request status** for worktree sessions, through the GitHub CLI. The sidebar and header show the pull request's number and state (checks running or failing, changes requested, approved, merged), the header chip lists each check, and the session asks for your attention when checks fail, a review arrives or the pull request is merged. Without `gh`, nothing changes. ([#2](https://github.com/onurozcan/omux/pull/2))
- **Clean up merged worktrees.** When a worktree session's pull request is merged, the sidebar, the session menu and the pull request chip offer to close the session and delete its worktree and branch. Closing a merged session says so instead of listing commits that a squash merge leaves behind. ([#3](https://github.com/onurozcan/omux/pull/3))
- **Notifications.** A macOS notification when a session you aren't looking at needs you, such as a tool permission. Clicking it opens the session. Optional notifications when a session finishes, and for notifications programs send with OSC 9 or OSC 777, such as Claude Code's (and its plugins') when its notification channel is `ghostty` or `iterm2`. Each kind can be turned off in Settings › Notifications. ([#4](https://github.com/onurozcan/omux/pull/4))

### Fixed

- The window's close, minimize and full screen buttons were missing on some macOS versions.
- Scanning a session's output for file paths and local URLs read the terminal from a background thread ([#1](https://github.com/onurozcan/omux/issues/1)).

## [0.2.0] - 2026-10-28

### Added

- **Worktree sessions** (⌥⌘T). A session can get its own Git branch and folder, next to the repo in `<repo>.worktrees/<branch>`, so parallel sessions don't touch each other's files. A project setting makes it the default for new sessions. Right-click a worktree session to create a pull request, merge it into its base branch or open its folder. Closing one asks whether to keep or delete the worktree.
- **Tabs in the right panel:** Files (⌥⌘1), Changes (⌥⌘2) and Browser (⌥⌘3).
- **Changes tab.** Shows the session's diff: against the base branch for worktree sessions, uncommitted changes otherwise. Click a line to leave a note, then send all notes to Claude in one message.
- **Browser tab.** A web view per session that opens the local server the session prints, such as `localhost:3000`. The pick button adds an element's HTML, styles and a screenshot to the prompt.
- **Allow and Deny** in the sidebar and the session header when Claude asks for a tool permission, so you can answer without switching sessions.
- **Subagents.** The selected session lists its running subagents and what each one is doing. Other sessions show a count. The header's agents button lists them all, and clicking one opens its live transcript.

### Fixed

- Restarting a session could mark it as stopped when the old terminal's exit arrived after the new one started.

## [0.1.0] - 2026-10-27

First public release.

- Projects and sessions in a sidebar, with drag to reorder, rename and colors.
- Claude conversations that resume after you quit and reopen omux.
- Live status for each session (working, needs you, done) and a Dock badge for sessions waiting on you.
- Files panel that previews Markdown, code and text, images, PDFs, audio, video and anything Quick Look can show.
- Recent Claude conversations (⇧⌘O), Claude plan usage limits in the sidebar, themes, terminal font settings and a quick switcher (⌘K).
- Open folders from Finder's "Open in omux" service, the Dock icon or `open -a omux <folder>`.

[0.3.0]: https://github.com/onurozcan/omux/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/onurozcan/omux/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/onurozcan/omux/releases/tag/v0.1.0
