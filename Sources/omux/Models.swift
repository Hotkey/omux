import Foundation
import SwiftUI

enum TabColor: String, Codable, CaseIterable, Identifiable {
    case none, red, orange, yellow, green, mint, teal, cyan, blue, indigo, purple, pink, brown, gray

    var id: String { rawValue }

    var color: Color? {
        switch self {
        case .none: return nil
        case .red: return .red
        case .orange: return .orange
        case .yellow: return .yellow
        case .green: return .green
        case .mint: return .mint
        case .teal: return .teal
        case .cyan: return .cyan
        case .blue: return .blue
        case .indigo: return .indigo
        case .purple: return .purple
        case .pink: return .pink
        case .brown: return .brown
        case .gray: return .gray
        }
    }

    var label: String { self == .none ? "None" : rawValue.capitalized }
}

struct TabItem: Identifiable, Codable, Hashable {
    var id = UUID()
    var name: String
    /// nil inherits the group's directory (or the home folder).
    var directory: String?
    /// nil inherits the group's command; "" means a plain shell.
    var command: String?
    var color: TabColor = .none
    /// Stable Claude Code session id so the conversation resumes across relaunches.
    var claudeSessionID = UUID()

    init(name: String, directory: String? = nil, command: String? = nil) {
        self.name = name
        self.directory = directory
        self.command = command
    }

    // Every field is optional on decode so saved workspaces survive schema changes.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? "Session"
        directory = try c.decodeIfPresent(String.self, forKey: .directory)
        command = try c.decodeIfPresent(String.self, forKey: .command)
        color = try c.decodeIfPresent(TabColor.self, forKey: .color) ?? .none
        claudeSessionID = try c.decodeIfPresent(UUID.self, forKey: .claudeSessionID) ?? UUID()
    }
}

struct TabGroup: Identifiable, Codable, Hashable {
    var id = UUID()
    var name: String
    var directory: String?
    var command: String? = "claude"
    var isCollapsed = false
    var tabs: [TabItem] = []

    init(name: String, directory: String? = nil, tabs: [TabItem] = []) {
        self.name = name
        self.directory = directory
        self.tabs = tabs
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? "Project"
        directory = try c.decodeIfPresent(String.self, forKey: .directory)
        command = try c.decodeIfPresent(String.self, forKey: .command)
        isCollapsed = try c.decodeIfPresent(Bool.self, forKey: .isCollapsed) ?? false
        tabs = try c.decodeIfPresent([TabItem].self, forKey: .tabs) ?? []
    }
}

struct Workspace: Codable {
    var groups: [TabGroup] = []
    var selectedTabID: UUID?
}

/// Live, non-persisted state of one terminal tab.
struct TabRuntime: Equatable {
    var title = ""
    var cwd: String?
    var hasActivity = false
    var needsAttention = false
    var exited = false
    /// A program (e.g. claude) is running in the shell, as opposed to an idle prompt.
    var busy = false
    var statusState: StatusState?
    var statusDetail: String?

    enum StatusState: String { case idle, working, done, blocked, error }

    func statusLine(showFolder: Bool = true) -> String? {
        if let s = statusState {
            switch s {
            case .blocked: return statusDetail.map { "Needs you · \($0)" } ?? "Needs you"
            case .working: return statusDetail.map { "Working · \($0)" } ?? "Working…"
            case .done: return "Done"
            case .error: return statusDetail.map { "Error · \($0)" } ?? "Error"
            case .idle: break
            }
        }
        if exited { return "Stopped" }
        if !title.isEmpty { return title }
        if showFolder, let cwd { return (cwd as NSString).abbreviatingWithTildeInPath }
        return nil
    }

    var statusColor: Color? {
        switch statusState {
        case .blocked: return .orange
        case .working: return .blue
        case .done: return .green
        case .error: return .red
        default: return nil
        }
    }
}

enum Persistence {
    static let directory: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let dir = base.appendingPathComponent("omux", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }()

    static var workspaceURL: URL { directory.appendingPathComponent("workspace.json") }

    static var backupURL: URL { directory.appendingPathComponent("workspace.json.bak") }

    /// Falls back to the previous save if the current file is missing or unreadable.
    static func loadWorkspace() -> Workspace? {
        for url in [workspaceURL, backupURL] {
            if let data = try? Data(contentsOf: url),
               let workspace = try? JSONDecoder().decode(Workspace.self, from: data),
               !workspace.groups.isEmpty {
                return workspace
            }
        }
        return nil
    }

    static func save(_ workspace: Workspace) {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(workspace) else { return }
        if let current = try? Data(contentsOf: workspaceURL), current != data {
            try? current.write(to: backupURL, options: .atomic)
        }
        try? data.write(to: workspaceURL, options: .atomic)
    }
}
