import AppKit
import SwiftUI

// MARK: - Actions

extension Store {
    /// The folder a session runs in, with ~ expanded.
    func directory(of tab: TabItem) -> String? {
        (tab.directory ?? group(containing: tab.id)?.directory).map { ($0 as NSString).expandingTildeInPath }
    }

    /// Creates the worktree off the main thread, then adds a session that runs in it.
    /// `completion` gets an error message, or nil on success.
    func createWorktreeSession(groupID: UUID, name: String, branch: String, base: String, completion: @escaping (String?) -> Void) {
        guard let dir = groups.first(where: { $0.id == groupID })?.directory.map({ ($0 as NSString).expandingTildeInPath }) else {
            completion("This project has no folder.")
            return
        }
        Task.detached {
            guard let repo = Git.topLevel(dir) else {
                await MainActor.run { completion("\(dir) isn't a Git repository.") }
                return
            }
            let result = Git.addWorktree(repo: repo, branch: branch, base: base)
            let path = Git.worktreePath(repo: repo, branch: branch)
            await MainActor.run {
                guard result.ok else { completion(result.message); return }
                let tab = TabItem(name: name, directory: path, worktree: Worktree(branch: branch, base: base, repo: repo))
                self.addSession(tab, to: groupID)
                completion(nil)
            }
        }
    }

    /// ⌘W: a worktree session asks whether to keep its worktree; others close at once.
    func requestClose(_ id: UUID) {
        guard let tab = tab(id), let worktree = tab.worktree, let path = directory(of: tab),
              FileManager.default.fileExists(atPath: path) else {
            closeTab(id)
            return
        }
        let dirty = Git.run(["status", "--porcelain"], in: path).out.split(separator: "\n").count
        let ahead = Int(Git.run(["rev-list", "--count", "\(worktree.base)..\(worktree.branch)"], in: worktree.repo).out
            .trimmingCharacters(in: .whitespacesAndNewlines)) ?? 0
        var facts: [String] = []
        if dirty > 0 { facts.append("\(dirty) uncommitted file\(dirty == 1 ? "" : "s")") }
        if ahead > 0 { facts.append("\(ahead) commit\(ahead == 1 ? "" : "s") not in \(worktree.base)") }

        let alert = NSAlert()
        alert.messageText = "Close “\(tab.name)”?"
        alert.informativeText = facts.isEmpty
            ? "Its worktree on “\(worktree.branch)” has nothing that isn't already in \(worktree.base)."
            : "Its worktree on “\(worktree.branch)” has \(facts.joined(separator: " and "))."
        alert.addButton(withTitle: "Keep Worktree")
        let delete = alert.addButton(withTitle: "Delete Worktree and Branch")
        delete.hasDestructiveAction = true
        alert.addButton(withTitle: "Cancel")
        switch alert.runModal() {
        case .alertFirstButtonReturn:
            closeTab(id)
        case .alertSecondButtonReturn:
            closeTab(id)
            Task.detached {
                let result = Git.removeWorktree(worktree, path: path)
                if !result.ok {
                    await MainActor.run { Self.showError("Couldn't delete the worktree", result.message) }
                }
            }
        default:
            break
        }
    }

    /// Commits, pushes the branch and opens GitHub's pull request page.
    func createPullRequest(_ id: UUID) {
        guard let tab = tab(id), let worktree = tab.worktree, let path = directory(of: tab),
              commitIfNeeded(path: path, branch: worktree.branch, suggestion: tab.name) else { return }
        gitActivity[id] = "Pushing \(worktree.branch)…"
        Task.detached {
            let (result, url) = Git.pushForPullRequest(worktree, path: path)
            await MainActor.run {
                self.gitActivity[id] = nil
                if !result.ok {
                    Self.showError("Couldn't push “\(worktree.branch)”", result.message)
                } else if let url {
                    NSWorkspace.shared.open(url)
                } else {
                    Self.showInfo("Pushed “\(worktree.branch)”", "The remote isn't on GitHub, so open the pull request on your host.")
                }
            }
        }
    }

    /// Merges the session's branch into its base branch in the main checkout.
    func mergeWorktree(_ id: UUID) {
        guard let tab = tab(id), let worktree = tab.worktree, let path = directory(of: tab),
              commitIfNeeded(path: path, branch: worktree.branch, suggestion: tab.name) else { return }
        gitActivity[id] = "Merging into \(worktree.base)…"
        Task.detached {
            let result = Git.merge(worktree)
            await MainActor.run {
                self.gitActivity[id] = nil
                if result.ok {
                    Self.showInfo("Merged “\(worktree.branch)” into “\(worktree.base)”", result.out.trimmingCharacters(in: .whitespacesAndNewlines))
                } else {
                    Self.showError("Couldn't merge “\(worktree.branch)”", result.message)
                }
            }
        }
    }

    /// Offers to commit uncommitted work first. Returns false when the user cancels or the
    /// commit fails.
    private func commitIfNeeded(path: String, branch: String, suggestion: String) -> Bool {
        let dirty = Git.run(["status", "--porcelain"], in: path).out.split(separator: "\n").count
        guard dirty > 0 else { return true }
        let alert = NSAlert()
        alert.messageText = "Commit \(dirty) changed file\(dirty == 1 ? "" : "s") first?"
        alert.informativeText = "They'll be committed to “\(branch)”. Uncommitted changes aren't part of a merge or pull request."
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 300, height: 24))
        field.stringValue = suggestion
        field.placeholderString = "Commit message"
        alert.accessoryView = field
        alert.addButton(withTitle: "Commit")
        alert.addButton(withTitle: "Cancel")
        alert.window.initialFirstResponder = field
        guard alert.runModal() == .alertFirstButtonReturn else { return false }
        let message = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        let result = Git.commitAll(in: path, message: message.isEmpty ? suggestion : message)
        if !result.ok { Self.showError("Couldn't commit", result.message) }
        return result.ok
    }

    static func showError(_ title: String, _ detail: String) {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = title
        alert.informativeText = detail
        alert.runModal()
    }

    static func showInfo(_ title: String, _ detail: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = detail
        alert.runModal()
    }
}

// MARK: - New session sheet

struct WorktreeSheet: View {
    @EnvironmentObject var store: Store
    @Environment(\.dismiss) private var dismiss
    let groupID: UUID

    @State private var name = ""
    @State private var branch = ""
    @State private var branchEdited = false
    @State private var base = ""
    @State private var branches: [String] = []
    @State private var repo: String?
    @State private var loaded = false
    @State private var error: String?
    @State private var working = false

    private var group: TabGroup? { store.groups.first { $0.id == groupID } }

    private var canCreate: Bool {
        repo != nil && !working && !name.trimmingCharacters(in: .whitespaces).isEmpty
            && !branch.isEmpty && !branch.contains(" ") && !base.isEmpty
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            VStack(alignment: .leading, spacing: 4) {
                Text("New Session in Worktree").font(.headline)
                Text("Claude works on its own branch in its own folder, so sessions don't touch each other's files.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if loaded && repo == nil {
                Label("\(group?.directory ?? "This project's folder") isn't a Git repository.", systemImage: "exclamationmark.triangle")
                    .foregroundStyle(.orange)
            } else {
                Form {
                    TextField("Name", text: $name)
                    LabeledContent("Branch") {
                        TextField("Branch", text: Binding(get: { branch }, set: { branch = $0; branchEdited = true }))
                            .labelsHidden()
                            .font(.system(.body, design: .monospaced))
                    }
                    Picker("From", selection: $base) {
                        ForEach(branches, id: \.self) { Text($0).tag($0) }
                    }
                    if let repo, !branch.isEmpty {
                        LabeledContent("Folder") {
                            Text((Git.worktreePath(repo: repo, branch: branch) as NSString).abbreviatingWithTildeInPath)
                                .font(.caption.monospaced())
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                                .truncationMode(.head)
                        }
                    }
                }
                Toggle("Use worktrees for new sessions in \(group?.name ?? "this project")", isOn: Binding(
                    get: { group?.useWorktrees ?? false },
                    set: { value in store.updateGroup(groupID) { $0.useWorktrees = value } }
                ))
            }
            if let error {
                Text(error)
                    .font(.callout)
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                Spacer()
                if working { ProgressView().controlSize(.small) }
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Create") { create() }.keyboardShortcut(.defaultAction).disabled(!canCreate)
            }
        }
        .padding(20)
        .frame(width: 460)
        .onChange(of: name) { _, value in if !branchEdited { branch = Git.slug(value) } }
        .task { await load() }
    }

    private func load() async {
        name = "Session \((group?.tabs.count ?? 0) + 1)"
        guard let dir = group?.directory.map({ ($0 as NSString).expandingTildeInPath }) else { loaded = true; return }
        let info = await Task.detached { () -> (String?, [String], String?) in
            guard let repo = Git.topLevel(dir) else { return (nil, [], nil) }
            return (repo, Git.branches(repo), Git.currentBranch(repo))
        }.value
        repo = info.0
        branches = info.1
        base = info.2 ?? info.1.first ?? ""
        loaded = true
    }

    private func create() {
        working = true
        error = nil
        let finalName = name.trimmingCharacters(in: .whitespaces)
        store.createWorktreeSession(groupID: groupID, name: finalName, branch: branch, base: base) { message in
            working = false
            if let message { error = message } else { dismiss() }
        }
    }
}

/// Shown in the header strip of a worktree session; opens the Changes tab.
struct BranchChip: View {
    @EnvironmentObject var store: Store
    @ObservedObject private var changes = Changes.shared
    let tabID: UUID
    let worktree: Worktree

    var body: some View {
        Button { store.showPanel(.changes) } label: {
            HStack(spacing: 5) {
                Image(systemName: "arrow.triangle.branch")
                Text(worktree.branch).lineLimit(1)
                if changes.tabID == tabID, !changes.files.isEmpty {
                    let totals = changes.totals
                    Text("+\(totals.added)").foregroundStyle(.green)
                    Text("−\(totals.removed)").foregroundStyle(.red)
                }
            }
            .padding(.horizontal, 7)
            .frame(height: 18)
            .overlay(Capsule().strokeBorder(.primary.opacity(0.15)))
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .fixedSize()
        .help("Changes on \(worktree.branch) since \(worktree.base) (⌥⌘2)")
    }
}

/// Allow / Deny for Claude Code's permission prompt, without switching to the session.
struct PermissionButtons: View {
    @EnvironmentObject var store: Store
    let tabID: UUID

    var body: some View {
        HStack(spacing: 6) {
            Button("Allow") { store.answerPermission(tabID, allow: true) }
                .buttonStyle(.borderedProminent)
            Button("Deny") { store.answerPermission(tabID, allow: false) }
                .buttonStyle(.bordered)
        }
        .controlSize(.small)
    }
}
