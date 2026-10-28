import AppKit
import Combine
import SwiftUI

extension UUID: Identifiable {
    public var id: UUID { self }
}

@MainActor
final class Store: ObservableObject {
    static let shared = Store()

    @Published var groups: [TabGroup]
    @Published var selectedTabID: UUID? {
        didSet {
            // Deferred: this fires inside the List's selection update.
            guard let id = selectedTabID else { return }
            DispatchQueue.main.async { [weak self] in
                self?.clearAttention(id)
                self?.refreshBusy(id)
            }
        }
    }
    @Published private(set) var runtime: [UUID: TabRuntime] = [:] {
        didSet {
            let count = runtime.values.filter(\.needsAttention).count
            NSApp.dockTile.badgeLabel = count > 0 ? "\(count)" : nil
        }
    }
    @Published var renamingID: UUID?
    @Published var editingTabID: UUID?
    @Published var editingGroupID: UUID?
    @Published var showQuickSwitcher = false
    @Published var showRecentSessions = false
    /// A file the user ⌘-clicked in a terminal; the files panel shows it.
    @Published var previewRequest: String?
    /// File paths mentioned in the selected session's output, newest last.
    @Published private(set) var mentionedPaths: [String] = []
    /// Local server URLs (localhost, …) in the selected session's output, newest last.
    @Published private(set) var mentionedURLs: [String] = []
    /// The project a "New Session in Worktree" sheet is open for.
    @Published var newWorktreeGroupID: UUID?
    /// The subagent whose transcript the files panel shows.
    @Published var openAgent: AgentRef?
    /// A git action in progress per session ("Pushing…"), shown in the Changes tab.
    @Published var gitActivity: [UUID: String] = [:]
    /// One web view per session, kept while the session lives.
    private var browsers: [UUID: BrowserSession] = [:]

    private var terminals: [UUID: TermView] = [:]
    private var cancellables = Set<AnyCancellable>()

    private init() {
        let saved = Persistence.loadWorkspace()
        var groups = saved?.groups ?? []
        if groups.isEmpty {
            groups = [TabGroup(name: "Default", tabs: [TabItem(name: "Session 1")])]
        }
        self.groups = groups
        let allIDs = Set(groups.flatMap { $0.tabs.map(\.id) })
        if let sel = saved?.selectedTabID, allIDs.contains(sel) {
            selectedTabID = sel
        } else {
            selectedTabID = groups.first(where: { !$0.tabs.isEmpty })?.tabs.first?.id
        }

        $groups.dropFirst()
            .debounce(for: .milliseconds(400), scheduler: RunLoop.main)
            .sink { [weak self] _ in self?.save() }
            .store(in: &cancellables)
        $selectedTabID.dropFirst()
            .debounce(for: .milliseconds(400), scheduler: RunLoop.main)
            .sink { [weak self] _ in self?.save() }
            .store(in: &cancellables)

        NotificationCenter.default.addObserver(forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main) { _ in
            DispatchQueue.main.async {
                if let id = Store.shared.selectedTabID { Store.shared.clearAttention(id) }
            }
        }

        if saved == nil { save() }
        if UserDefaults.standard.bool(forKey: Prefs.autoResume) {
            startAllSessions()
        } else {
            // Restored tabs stay dormant until selected (shell) or played (claude).
            for entry in allTabs { runtime[entry.tab.id] = TabRuntime(exited: true) }
        }
    }

    // MARK: Lookup

    func location(of tabID: UUID) -> (group: Int, tab: Int)? {
        for (g, group) in groups.enumerated() {
            if let t = group.tabs.firstIndex(where: { $0.id == tabID }) { return (g, t) }
        }
        return nil
    }

    func tab(_ id: UUID) -> TabItem? {
        location(of: id).map { groups[$0.group].tabs[$0.tab] }
    }

    func group(containing tabID: UUID) -> TabGroup? {
        location(of: tabID).map { groups[$0.group] }
    }

    var selectedTab: TabItem? { selectedTabID.flatMap(tab) }

    var selectedGroup: TabGroup? {
        selectedTabID.flatMap(group(containing:)) ?? groups.first
    }

    var allTabs: [(tab: TabItem, group: TabGroup)] {
        groups.flatMap { g in g.tabs.map { ($0, g) } }
    }

    var runningSessionCount: Int {
        terminals.keys.filter { !(runtime[$0]?.exited ?? false) }.count
    }

    // MARK: Terminal lifecycle

    /// The live view for a tab; a dormant tab gets a plain shell (no claude) on first show.
    func terminal(for tabID: UUID) -> TermView? {
        if let view = terminals[tabID] { return view }
        guard let loc = location(of: tabID) else { return nil }
        return makeTerminal(tab: groups[loc.group].tabs[loc.tab], group: groups[loc.group], command: "")
    }

    private func startAllSessions() {
        let all = allTabs
        let ordered = all.filter { $0.tab.id == selectedTabID } + all.filter { $0.tab.id != selectedTabID }
        var delay: TimeInterval = 0
        for entry in ordered {
            let id = entry.tab.id
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
                guard let self, self.terminals[id] == nil, let loc = self.location(of: id) else { return }
                _ = self.makeTerminal(tab: self.groups[loc.group].tabs[loc.tab], group: self.groups[loc.group])
            }
            delay += 0.25
        }
    }

    @discardableResult
    private func makeTerminal(tab: TabItem, group: TabGroup, command: String? = nil) -> TermView {
        let view = TermView(tabID: tab.id)
        terminals[tab.id] = view
        // A replaced view (restart) still reports its exit; only the current one may update the tab.
        view.onEvent = { [weak self, weak view] event in
            guard let self, let view, self.terminals[tab.id] === view else { return }
            self.handle(event, for: tab.id)
        }
        launch(view, tab: tab, group: group, command: command)
        return view
    }

    private func launch(_ view: TermView, tab: TabItem, group: TabGroup, command: String?) {
        let home = NSHomeDirectory()
        var dir = ((tab.directory ?? group.directory) as NSString?)?.expandingTildeInPath ?? home
        var isDir: ObjCBool = false
        if !FileManager.default.fileExists(atPath: dir, isDirectory: &isDir) || !isDir.boolValue { dir = home }

        let inherited = ProcessInfo.processInfo.environment
        let shell = inherited["SHELL"] ?? "/bin/zsh"
        // A clean login environment, like Terminal.app: nothing leaks in from whoever
        // launched omux (e.g. a Claude session's CLAUDE_CODE_* markers).
        var env: [String: String] = [
            "HOME": home,
            "USER": inherited["USER"] ?? NSUserName(),
            "LOGNAME": inherited["LOGNAME"] ?? NSUserName(),
            "SHELL": shell,
            "TMPDIR": inherited["TMPDIR"] ?? NSTemporaryDirectory(),
            "PATH": "/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin",
            "LANG": inherited["LANG"] ?? "en_US.UTF-8",
            "TERM": "xterm-256color",
            "COLORTERM": "truecolor",
            "TERM_PROGRAM": "omux",
        ]
        if let enc = inherited["__CF_USER_TEXT_ENCODING"] { env["__CF_USER_TEXT_ENCODING"] = enc }

        view.startProcess(
            executable: shell,
            args: ["-l"],
            environment: env.map { "\($0.key)=\($0.value)" },
            execName: "-" + (shell as NSString).lastPathComponent,
            currentDirectory: dir
        )
        update(tab.id) {
            $0.exited = false; $0.cwd = dir; $0.title = ""
            $0.statusState = nil; $0.statusKind = nil; $0.statusDetail = nil; $0.statusChildren = []
        }

        if let command = command ?? startupCommand(tab: tab, group: group), !command.isEmpty {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
                view.send(txt: command + "\n")
            }
        }
    }

    /// Kills the shell and everything it spawned (claude, node, …), not just the shell.
    private func killTree(_ view: TermView) {
        let pid = view.process.shellPid
        if pid > 0 { Darwin.kill(-pid, SIGHUP) }
        view.terminate()
    }

    /// Stops a session: its whole process tree is killed and the tab shows as stopped.
    /// Interrupts whatever runs in the shell, like Ctrl-C. Sent twice because Claude Code
    /// asks for a second Ctrl-C to exit. The shell itself stays at its prompt.
    func stop(_ id: UUID) {
        guard let view = terminals[id], !(runtime[id]?.exited ?? true) else { return }
        view.send(txt: "\u{03}")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { view.send(txt: "\u{03}") }
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in self?.refreshBusy(id) }
    }

    /// Force-kills the session's whole process tree; the tab shows as exited.
    func kill(_ id: UUID) {
        guard let view = terminals[id] else { return }
        killTree(view)
        update(id) { $0.exited = true; $0.statusState = nil; $0.busy = false }
    }

    /// Rescans the selected session's scrollback for file paths and local URLs (background, cheap).
    func scanMentionedPaths() {
        guard let id = selectedTabID, let view = terminals[id] else { mentionedPaths = []; mentionedURLs = []; return }
        // Read the buffer on the main actor; match and check files off it.
        let text = view.scrollbackText()
        Task { [weak self] in
            let (paths, urls) = await Task.detached {
                (TermView.mentionedPaths(in: text), TermView.mentionedLocalURLs(in: text))
            }.value
            guard let self, self.selectedTabID == id else { return }
            if self.mentionedPaths != paths { self.mentionedPaths = paths }
            if self.mentionedURLs != urls { self.mentionedURLs = urls }
        }
    }

    /// Types text into a session's prompt as a paste; `submit` presses Return after it.
    func sendText(_ text: String, to id: UUID, submit: Bool) {
        guard let view = terminals[id], !(runtime[id]?.exited ?? true) else { return }
        view.paste(text)
        if submit {
            // A beat later, so the program has taken the paste before Return arrives.
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) { view.send(txt: "\r") }
        }
    }

    /// Answers Claude Code's permission prompt: Return picks its default "Yes",
    /// Escape is its "No, and tell Claude what to do differently".
    func answerPermission(_ id: UUID, allow: Bool) {
        guard let view = terminals[id], runtime[id]?.awaitsPermission == true else { return }
        view.send(txt: allow ? "\r" : "\u{1b}")
    }

    /// Shows the right panel on the given tab.
    func showPanel(_ tab: PanelTab) {
        UserDefaults.standard.set(true, forKey: Prefs.showFiles)
        UserDefaults.standard.set(tab.rawValue, forKey: Prefs.panelTab)
    }

    func openAgentTranscript(_ agent: SubAgent, in tabID: UUID) {
        openAgent = AgentRef(tabID: tabID, agent: agent)
        showPanel(.files)
    }

    func browser(for id: UUID) -> BrowserSession {
        if let browser = browsers[id] { return browser }
        let browser = BrowserSession(tabID: id)
        browsers[id] = browser
        return browser
    }

    /// Re-checks whether the shell has a child process (claude, a build, …).
    func refreshBusy(_ id: UUID) {
        guard let view = terminals[id], !(runtime[id]?.exited ?? true) else { return }
        let pid = view.process.shellPid
        let busy = pid > 0 && Self.hasChildProcess(of: pid)
        update(id) { $0.busy = busy }
    }

    private static func hasChildProcess(of pid: pid_t) -> Bool {
        var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_ALL, 0]
        var size = 0
        guard sysctl(&mib, 4, nil, &size, nil, 0) == 0, size > 0 else { return false }
        var procs = [kinfo_proc](repeating: kinfo_proc(), count: size / MemoryLayout<kinfo_proc>.stride + 8)
        size = procs.count * MemoryLayout<kinfo_proc>.stride
        guard sysctl(&mib, 4, &procs, &size, nil, 0) == 0 else { return false }
        return procs.prefix(size / MemoryLayout<kinfo_proc>.stride).contains { $0.kp_eproc.e_ppid == pid }
    }

    /// Continues the tab's Claude conversation: relaunches the tab if it was stopped,
    /// otherwise runs the command in its live shell.
    func start(_ id: UUID) {
        guard let loc = location(of: id) else { return }
        let tab = groups[loc.group].tabs[loc.tab]
        let session = tab.claudeSessionID.uuidString.lowercased()
        let command = Self.claudeSessionExists(session) ? "claude --resume \(session)" : "claude --continue"
        if let view = terminals[id], !(runtime[id]?.exited ?? true) {
            guard !(runtime[id]?.busy ?? false) else { return }
            view.send(txt: command + "\n")
        } else {
            terminals.removeValue(forKey: id)?.removeFromSuperview()
            makeTerminal(tab: tab, group: groups[loc.group], command: command)
            objectWillChange.send()
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in self?.refreshBusy(id) }
    }

    /// Resolves the tab's command and, for `claude`, pins it to the tab's session id so the
    /// conversation resumes on relaunch.
    func startupCommand(tab: TabItem, group: TabGroup) -> String? {
        let raw = (tab.command ?? group.command ?? "").trimmingCharacters(in: .whitespaces)
        guard !raw.isEmpty else { return nil }
        let parts = raw.split(separator: " ", maxSplits: 1).map(String.init)
        guard parts[0] == "claude" else { return raw }
        let rest = parts.count > 1 ? parts[1] : ""
        let words = Set(rest.split(separator: " ").map(String.init))
        if !words.isDisjoint(with: ["--resume", "-r", "--continue", "-c", "--session-id"]) { return raw }
        let session = tab.claudeSessionID.uuidString.lowercased()
        let flag = Self.claudeSessionExists(session) ? "--resume \(session)" : "--session-id \(session)"
        return ["claude", flag, rest].filter { !$0.isEmpty }.joined(separator: " ")
    }

    /// Claude Code writes each conversation to ~/.claude/projects/<project>/<session>.jsonl.
    private static func claudeSessionExists(_ session: String) -> Bool {
        let projects = NSHomeDirectory() + "/.claude/projects"
        guard let dirs = try? FileManager.default.contentsOfDirectory(atPath: projects) else { return false }
        return dirs.contains { FileManager.default.fileExists(atPath: "\(projects)/\($0)/\(session).jsonl") }
    }

    /// Opens a folder as a new group with one Claude session inside it.
    func openFolder(_ path: String) {
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: path, isDirectory: &isDir), isDir.boolValue else { return }
        if let existing = groups.first(where: { $0.directory == path }) {
            newTab(in: existing.id)
            return
        }
        let group = TabGroup(name: (path as NSString).lastPathComponent, directory: path, tabs: [TabItem(name: "Session 1")])
        groups.append(group)
        makeTerminal(tab: group.tabs[0], group: group)
        selectedTabID = group.tabs[0].id
    }

    /// Opens an existing Claude conversation as a session in the project for its folder.
    func openClaudeSession(_ recent: RecentSession) {
        if let existing = allTabs.first(where: { $0.tab.claudeSessionID == recent.id }) {
            selectedTabID = existing.tab.id
            return
        }
        let gi: Int
        if let index = groups.firstIndex(where: { $0.directory == recent.cwd }) {
            gi = index
        } else {
            groups.append(TabGroup(name: (recent.cwd as NSString).lastPathComponent, directory: recent.cwd))
            gi = groups.count - 1
        }
        var tab = TabItem(name: recent.tabName.isEmpty ? "Session" : recent.tabName)
        tab.claudeSessionID = recent.id
        groups[gi].tabs.append(tab)
        groups[gi].isCollapsed = false
        makeTerminal(tab: tab, group: groups[gi])
        selectedTabID = tab.id
    }

    func openFolderPanel() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = true
        panel.prompt = "Open"
        guard panel.runModal() == .OK else { return }
        panel.urls.forEach { openFolder($0.path) }
    }

    private func handle(_ event: TermEvent, for id: UUID) {
        switch event {
        case .bell:
            if !isWatching(id) { update(id) { $0.needsAttention = true } }
        case .title(let title):
            refreshBusy(id)
            // Claude Code rewrites the title as its state changes; treat that as activity.
            update(id) {
                if $0.title != title, !isWatching(id) { $0.hasActivity = true }
                $0.title = title
            }
        case .directory(let dir):
            update(id) { $0.cwd = dir }
        case .exited:
            update(id) { $0.exited = true; $0.statusState = nil; $0.statusKind = nil; $0.statusChildren = []; $0.busy = false }
        case .openPath(let path):
            previewRequest = path
        case .status(let state, let kind, let message, let children):
            let previous = runtime[id]?.statusState
            update(id) {
                $0.statusState = state
                $0.statusKind = kind
                $0.statusDetail = message
                $0.statusChildren = children
                if state == .blocked || state == .done, !isWatching(id) { $0.needsAttention = true }
            }
            if state != previous { notifyStatusChange(id) }
        case .notification(let title, let body):
            guard !isWatching(id) else { return }
            update(id) { $0.needsAttention = true }
            if UserDefaults.standard.object(forKey: Prefs.notifyPrograms) as? Bool ?? true {
                notify(id, subtitle: title, body: body)
            }
        }
    }

    /// Notifies when a session you aren't looking at starts waiting on you, or finishes.
    /// Only on a change of state, so a session that stays blocked notifies once.
    private func notifyStatusChange(_ id: UUID) {
        guard !isWatching(id), let rt = runtime[id], let state = rt.statusState else { return }
        let enabled: Bool
        switch state {
        case .blocked: enabled = UserDefaults.standard.object(forKey: Prefs.notifyNeedsYou) as? Bool ?? true
        case .done, .error: enabled = UserDefaults.standard.bool(forKey: Prefs.notifyDone)
        case .idle, .working: enabled = false
        }
        guard enabled, let line = rt.statusLine(showFolder: false) else { return }
        notify(id, body: line)
    }

    /// The session's name as the title, its project (or the program's own title) under it.
    private func notify(_ id: UUID, subtitle: String? = nil, body: String) {
        guard let tab = tab(id) else { return }
        Notifier.shared.post(tabID: id, title: tab.name, subtitle: subtitle ?? group(containing: id)?.name, body: body)
    }

    /// Brings omux forward on a session, e.g. from a click on its notification.
    func reveal(_ id: UUID) {
        guard tab(id) != nil else { return }
        AppDelegate.openWindow?(id: "main")
        NSApp.activate()
        selectedTabID = id
    }

    private func isWatching(_ id: UUID) -> Bool {
        selectedTabID == id && NSApp.isActive && NSApp.keyWindow != nil
    }

    private func clearAttention(_ id: UUID) {
        update(id) { $0.hasActivity = false; $0.needsAttention = false }
        Notifier.shared.clear(tabID: id)
    }

    private func update(_ id: UUID, _ mutate: (inout TabRuntime) -> Void) {
        var value = runtime[id] ?? TabRuntime()
        let before = value
        mutate(&value)
        if value != before { runtime[id] = value }
    }

    // MARK: Tabs

    func newTab(in groupID: UUID? = nil, command: String? = nil) {
        guard let gi = groups.firstIndex(where: { $0.id == (groupID ?? selectedGroup?.id) }) ?? groups.indices.first else {
            newGroup()
            return
        }
        if command == nil, groups[gi].useWorktrees {
            newWorktreeGroupID = groups[gi].id
            return
        }
        let tab = TabItem(name: "Session \(groups[gi].tabs.count + 1)", command: command)
        groups[gi].tabs.append(tab)
        groups[gi].isCollapsed = false
        makeTerminal(tab: tab, group: groups[gi])
        selectedTabID = tab.id
    }

    /// Adds a ready-made session to a project and starts it.
    func addSession(_ tab: TabItem, to groupID: UUID) {
        guard let gi = groups.firstIndex(where: { $0.id == groupID }) else { return }
        groups[gi].tabs.append(tab)
        groups[gi].isCollapsed = false
        makeTerminal(tab: tab, group: groups[gi])
        selectedTabID = tab.id
    }

    func closeTab(_ id: UUID) {
        guard let loc = location(of: id) else { return }
        if let view = terminals.removeValue(forKey: id) {
            killTree(view)
            view.removeFromSuperview()
        }
        runtime[id] = nil
        Notifier.shared.clear(tabID: id)
        browsers.removeValue(forKey: id)?.close()
        if openAgent?.tabID == id { openAgent = nil }
        groups[loc.group].tabs.remove(at: loc.tab)
        if selectedTabID == id {
            let siblings = groups[loc.group].tabs
            selectedTabID = siblings.isEmpty ? allTabs.first?.tab.id : siblings[min(loc.tab, siblings.count - 1)].id
        }
    }

    func restart(_ id: UUID) {
        guard let loc = location(of: id) else { return }
        if let view = terminals.removeValue(forKey: id) {
            killTree(view)
            view.removeFromSuperview()
        }
        makeTerminal(tab: groups[loc.group].tabs[loc.tab], group: groups[loc.group])
        objectWillChange.send()
    }

    func rename(_ id: UUID, to name: String) {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return }
        if let loc = location(of: id) {
            groups[loc.group].tabs[loc.tab].name = trimmed
        } else if let gi = groups.firstIndex(where: { $0.id == id }) {
            groups[gi].name = trimmed
        }
    }

    func updateTab(_ id: UUID, _ mutate: (inout TabItem) -> Void) {
        guard let loc = location(of: id) else { return }
        mutate(&groups[loc.group].tabs[loc.tab])
    }

    func updateGroup(_ id: UUID, _ mutate: (inout TabGroup) -> Void) {
        guard let gi = groups.firstIndex(where: { $0.id == id }) else { return }
        mutate(&groups[gi])
    }

    func moveTabs(in groupID: UUID, from source: IndexSet, to destination: Int) {
        updateGroup(groupID) { $0.tabs.move(fromOffsets: source, toOffset: destination) }
    }

    func moveTab(_ id: UUID, toGroup groupID: UUID) {
        guard let loc = location(of: id), let gi = groups.firstIndex(where: { $0.id == groupID }), groups[loc.group].id != groupID else { return }
        let tab = groups[loc.group].tabs.remove(at: loc.tab)
        groups[gi].tabs.append(tab)
        groups[gi].isCollapsed = false
    }

    // MARK: Groups

    func newGroup() {
        let group = TabGroup(name: "Project \(groups.count + 1)", tabs: [TabItem(name: "Session 1")])
        groups.append(group)
        makeTerminal(tab: group.tabs[0], group: group)
        selectedTabID = group.tabs[0].id
        renamingID = group.id
    }

    func deleteGroup(_ id: UUID) {
        guard let gi = groups.firstIndex(where: { $0.id == id }) else { return }
        let running = groups[gi].tabs.filter { !(runtime[$0.id]?.exited ?? true) }.count
        if running > 0 {
            let alert = NSAlert()
            alert.messageText = "Delete “\(groups[gi].name)”?"
            alert.informativeText = "\(running) running session\(running == 1 ? "" : "s") will be closed."
            alert.addButton(withTitle: "Delete")
            alert.addButton(withTitle: "Cancel")
            guard alert.runModal() == .alertFirstButtonReturn else { return }
        }
        for tab in groups[gi].tabs { closeTab(tab.id) }
        groups.remove(at: gi)
        if groups.isEmpty { newGroup() }
    }

    /// Drops `groupID` at the position of `targetID`.
    func moveGroup(_ groupID: UUID, onto targetID: UUID) {
        guard groupID != targetID,
              let from = groups.firstIndex(where: { $0.id == groupID }),
              let to = groups.firstIndex(where: { $0.id == targetID }) else { return }
        let group = groups.remove(at: from)
        groups.insert(group, at: to)
    }

    /// Applies a drag in the flat sidebar list. Moving a project row reorders projects;
    /// moving a session row places it under whichever project row ends up above it.
    func moveRows(_ rows: [SidebarRow], from source: IndexSet, to destination: Int) {
        guard let first = source.first else { return }
        var moved = rows
        moved.move(fromOffsets: source, toOffset: destination)
        switch rows[first] {
        case .group:
            let order = moved.compactMap { row -> UUID? in
                if case .group(let g) = row { return g.id } else { return nil }
            }
            groups.sort { a, b in (order.firstIndex(of: a.id) ?? 0) < (order.firstIndex(of: b.id) ?? 0) }
        case .tab(let tab, let fromGroupID):
            guard let newIndex = moved.firstIndex(where: { $0.id == tab.id }) else { return }
            var targetID = groups.first?.id
            var position = 0
            for row in moved[..<newIndex].reversed() {
                if case .group(let g) = row { targetID = g.id; break }
                position += 1
            }
            guard let targetID, let gi = groups.firstIndex(where: { $0.id == targetID }),
                  let loc = location(of: tab.id) else { return }
            if fromGroupID == targetID {
                groups[gi].tabs.remove(at: loc.tab)
                groups[gi].tabs.insert(tab, at: min(position, groups[gi].tabs.count))
            } else {
                groups[loc.group].tabs.remove(at: loc.tab)
                groups[gi].tabs.insert(tab, at: min(position, groups[gi].tabs.count))
                groups[gi].isCollapsed = false
            }
        }
    }

    func moveGroup(_ groupID: UUID, by offset: Int) {
        guard let from = groups.firstIndex(where: { $0.id == groupID }) else { return }
        let to = from + offset
        guard groups.indices.contains(to) else { return }
        groups.swapAt(from, to)
    }

    func expandedBinding(_ groupID: UUID) -> Binding<Bool> {
        Binding(
            get: { !(self.groups.first(where: { $0.id == groupID })?.isCollapsed ?? false) },
            set: { expanded in self.updateGroup(groupID) { $0.isCollapsed = !expanded } }
        )
    }

    // MARK: Navigation

    func selectIndex(_ index: Int) {
        guard let group = selectedGroup, group.tabs.indices.contains(index) else { return }
        selectedTabID = group.tabs[index].id
    }

    func selectRelative(_ offset: Int) {
        let tabs = allTabs.map(\.tab.id)
        guard !tabs.isEmpty else { return }
        let current = selectedTabID.flatMap(tabs.firstIndex(of:)) ?? 0
        selectedTabID = tabs[(current + offset + tabs.count) % tabs.count]
    }

    // MARK: Persistence

    private func save() {
        Persistence.save(Workspace(groups: groups, selectedTabID: selectedTabID))
    }

    func applyAppearance() {
        let theme = Theme.current
        terminals.values.forEach { $0.apply(theme) }
    }

    /// Saves the layout and kills every session's process tree, running or not.
    func prepareForTermination() {
        save()
        terminals.values.forEach(killTree)
    }
}
