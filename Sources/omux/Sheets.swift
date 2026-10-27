import AppKit
import SwiftUI

struct QuickSwitcher: View {
    @EnvironmentObject var store: Store
    @Environment(\.dismiss) private var dismiss
    @State private var query = ""
    @FocusState private var focused: Bool

    private struct Match: Identifiable {
        let tab: TabItem
        let group: TabGroup
        var id: UUID { tab.id }
    }

    private var matches: [Match] {
        store.allTabs
            .map { Match(tab: $0.tab, group: $0.group) }
            .filter { m in
                query.isEmpty ||
                    "\(m.tab.name) \(m.group.name) \(store.runtime[m.tab.id]?.title ?? "")"
                    .localizedCaseInsensitiveContains(query)
            }
    }

    var body: some View {
        VStack(spacing: 0) {
            TextField("Jump to session…", text: $query)
                .textFieldStyle(.plain)
                .font(.title3)
                .padding(12)
                .focused($focused)
                .onSubmit { if let first = matches.first { pick(first.id) } }
            Divider()
            List(matches.prefix(15)) { match in
                Button { pick(match.id) } label: {
                    HStack {
                        Circle()
                            .fill(store.runtime[match.id]?.statusColor ?? match.tab.color.color ?? Color.secondary.opacity(0.35))
                            .frame(width: 8, height: 8)
                        Text(match.tab.name)
                        Text(match.group.name).foregroundStyle(.secondary)
                        Spacer()
                        if let line = store.runtime[match.id]?.statusLine() {
                            Text(line).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                        }
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
            .listStyle(.plain)
        }
        .frame(width: 460, height: 380)
        .onAppear { focused = true }
        .onExitCommand { dismiss() }
    }

    private func pick(_ id: UUID) {
        store.selectedTabID = id
        dismiss()
    }
}

private func chooseDirectory(current: String) -> String? {
    let panel = NSOpenPanel()
    panel.canChooseDirectories = true
    panel.canChooseFiles = false
    panel.allowsMultipleSelection = false
    if !current.isEmpty {
        panel.directoryURL = URL(fileURLWithPath: (current as NSString).expandingTildeInPath)
    }
    return panel.runModal() == .OK ? panel.url?.path : nil
}

struct TabEditSheet: View {
    @EnvironmentObject var store: Store
    @Environment(\.dismiss) private var dismiss
    let tabID: UUID

    private enum CommandMode: String, CaseIterable, Identifiable {
        case inherit = "Project default", shell = "Plain shell", custom = "Custom"
        var id: String { rawValue }
    }

    @State private var name = ""
    @State private var directory = ""
    @State private var mode: CommandMode = .inherit
    @State private var customCommand = ""
    @State private var sessionID = UUID()

    var body: some View {
        let group = store.group(containing: tabID)
        Form {
            TextField("Name", text: $name)
            HStack {
                TextField("Folder", text: $directory, prompt: Text(group?.directory ?? "~"))
                Button("Choose…") { if let dir = chooseDirectory(current: directory) { directory = dir } }
            }
            Picker("Command", selection: $mode) {
                ForEach(CommandMode.allCases) { Text($0.rawValue).tag($0) }
            }
            if mode == .custom {
                TextField("Command", text: $customCommand, prompt: Text("claude"))
            } else if mode == .inherit {
                Text(group?.command?.isEmpty == false ? group!.command! : "plain shell")
                    .font(.caption).foregroundStyle(.secondary)
            }
            LabeledContent("Claude session") {
                VStack(alignment: .leading, spacing: 4) {
                    Text(sessionID.uuidString.lowercased())
                        .font(.caption.monospaced())
                        .textSelection(.enabled)
                        .foregroundStyle(.secondary)
                    Button("Start a fresh conversation on next restart") {
                        sessionID = UUID()
                    }
                    .controlSize(.small)
                }
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Save") { save() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 460)
        .onAppear {
            guard let tab = store.tab(tabID) else { return }
            name = tab.name
            directory = tab.directory ?? ""
            switch tab.command {
            case nil: mode = .inherit
            case "": mode = .shell
            case let cmd?: mode = .custom; customCommand = cmd
            }
            sessionID = tab.claudeSessionID
        }
    }

    private func save() {
        store.updateTab(tabID) { tab in
            tab.name = name.trimmingCharacters(in: .whitespaces).isEmpty ? tab.name : name
            tab.directory = directory.isEmpty ? nil : directory
            switch mode {
            case .inherit: tab.command = nil
            case .shell: tab.command = ""
            case .custom: tab.command = customCommand.isEmpty ? nil : customCommand
            }
            tab.claudeSessionID = sessionID
        }
        dismiss()
    }
}

struct GroupEditSheet: View {
    @EnvironmentObject var store: Store
    @Environment(\.dismiss) private var dismiss
    let groupID: UUID

    @State private var name = ""
    @State private var directory = ""
    @State private var command = ""

    var body: some View {
        Form {
            TextField("Name", text: $name)
            HStack {
                TextField("Folder", text: $directory, prompt: Text("~"))
                Button("Choose…") { if let dir = chooseDirectory(current: directory) { directory = dir } }
            }
            TextField("Command for new sessions", text: $command, prompt: Text("leave empty for a plain shell"))
            Text("Applies when a session starts. Existing sessions keep running.")
                .font(.caption).foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Save") { save() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 460)
        .onAppear {
            guard let group = store.groups.first(where: { $0.id == groupID }) else { return }
            name = group.name
            directory = group.directory ?? ""
            command = group.command ?? ""
        }
    }

    private func save() {
        store.updateGroup(groupID) { group in
            group.name = name.trimmingCharacters(in: .whitespaces).isEmpty ? group.name : name
            group.directory = directory.isEmpty ? nil : directory
            group.command = command
        }
        dismiss()
    }
}

struct RecentSessionsSheet: View {
    @EnvironmentObject var store: Store
    @Environment(\.dismiss) private var dismiss
    @State private var sessions: [RecentSession] = []
    @State private var loaded = false
    @State private var query = ""
    @State private var byFolder = false
    @FocusState private var focused: Bool

    private var matches: [RecentSession] {
        sessions.filter { query.isEmpty || "\($0.prompt) \($0.folder)".localizedCaseInsensitiveContains(query) }
    }

    /// Folders ordered by their most recent session; sessions stay newest-first within each.
    private var folders: [(folder: String, sessions: [RecentSession])] {
        var order: [String] = []
        var grouped: [String: [RecentSession]] = [:]
        for session in matches {
            if grouped[session.folder] == nil { order.append(session.folder) }
            grouped[session.folder, default: []].append(session)
        }
        return order.map { ($0, grouped[$0]!) }
    }

    private var openIDs: Set<UUID> { Set(store.allTabs.map(\.tab.claudeSessionID)) }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                TextField("Search recent Claude sessions…", text: $query)
                    .textFieldStyle(.plain)
                    .font(.title3)
                    .focused($focused)
                    .onSubmit { if let first = matches.first { pick(first) } }
                Picker("", selection: $byFolder) {
                    Text("Recent").tag(false)
                    Text("By folder").tag(true)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
            }
            .padding(12)
            Divider()
            if !loaded {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if matches.isEmpty {
                Text(sessions.isEmpty ? "No Claude sessions found in ~/.claude/projects" : "No matches")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List {
                    if byFolder {
                        ForEach(folders, id: \.folder) { entry in
                            Section(entry.folder) {
                                ForEach(entry.sessions) { row($0, showFolder: false) }
                            }
                        }
                    } else {
                        ForEach(matches) { row($0, showFolder: true) }
                    }
                }
                .listStyle(.plain)
            }
        }
        .frame(width: 560, height: 440)
        .onExitCommand { dismiss() }
        .task {
            focused = true
            sessions = await Task.detached { RecentSession.load() }.value
            loaded = true
        }
    }

    private func row(_ session: RecentSession, showFolder: Bool) -> some View {
        Button { pick(session) } label: {
            VStack(alignment: .leading, spacing: 2) {
                HStack {
                    Text(session.prompt).lineLimit(1)
                    Spacer()
                    if openIDs.contains(session.id) {
                        Text("open").font(.caption2).foregroundStyle(.secondary)
                    }
                }
                HStack {
                    if showFolder { Text(session.folder) }
                    Spacer()
                    Text(session.date, format: .relative(presentation: .named))
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    private func pick(_ session: RecentSession) {
        store.openClaudeSession(session)
        dismiss()
    }
}
