import AppKit
import SwiftUI

/// The pull request for a worktree session's branch, as `gh pr view` reports it.
struct PullRequest: Equatable {
    enum State: String { case open = "OPEN", closed = "CLOSED", merged = "MERGED" }
    enum Review: String { case approved = "APPROVED", changesRequested = "CHANGES_REQUESTED", required = "REVIEW_REQUIRED" }
    enum Result { case pending, passed, failed, skipped }

    struct Check: Equatable, Identifiable {
        let id: Int
        let name: String
        let result: Result
        let url: URL?
    }

    let number: Int
    let title: String
    let url: URL
    let state: State
    let isDraft: Bool
    let review: Review?
    let checks: [Check]

    /// The checks taken together: any failure wins, then anything still running.
    var checksResult: Result? {
        let results = Set(checks.map(\.result))
        for result in [Result.failed, .pending, .passed] where results.contains(result) { return result }
        return nil
    }

    /// The most pressing fact about it, for tight spaces.
    var summary: String {
        switch state {
        case .merged: return "Merged"
        case .closed: return "Closed"
        case .open: break
        }
        if checksResult == .failed { return "Checks failing" }
        if review == .changesRequested { return "Changes requested" }
        if checksResult == .pending { return "Checks running" }
        if isDraft { return "Draft" }
        if review == .approved { return "Approved" }
        if checksResult == .passed { return "Checks passed" }
        return "Open"
    }

    var color: Color {
        switch state {
        case .merged: return .purple
        case .closed: return .secondary
        case .open: break
        }
        if checksResult == .failed { return .red }
        if review == .changesRequested { return .orange }
        if checksResult == .pending { return .blue }
        if isDraft { return .secondary }
        return .green
    }

    /// Changes worth pulling the user back to the session for.
    func needsAttention(since old: PullRequest) -> Bool {
        (checksResult == .failed && old.checksResult != .failed)
            || (review != old.review && (review == .changesRequested || review == .approved))
            || (state == .merged && old.state != .merged)
    }
}

/// Polls GitHub, through gh, for the pull requests of worktree sessions' branches.
/// Does nothing when gh isn't installed.
@MainActor
final class PullRequestMonitor: ObservableObject {
    static let shared = PullRequestMonitor()

    @Published private(set) var requests: [UUID: PullRequest] = [:]
    private let queue = DispatchQueue(label: "omux.pulls", qos: .utility)
    private var polling = false
    private var timer: Timer?

    private enum Lookup { case found(PullRequest), none, unknown }

    private init() {
        timer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { _ in
            Task { @MainActor in PullRequestMonitor.shared.poll() }
        }
        // Coming back from the browser is when a new pull request most likely exists.
        NotificationCenter.default.addObserver(forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main) { _ in
            Task { @MainActor in PullRequestMonitor.shared.poll() }
        }
        DispatchQueue.main.async { self.poll() }
    }

    func poll() {
        guard !polling else { return }
        let store = Store.shared
        let sessions = store.allTabs.compactMap { entry -> (id: UUID, dir: String)? in
            guard entry.tab.worktree != nil, let dir = store.directory(of: entry.tab),
                  FileManager.default.fileExists(atPath: dir) else { return nil }
            return (entry.tab.id, dir)
        }
        guard !sessions.isEmpty || !requests.isEmpty else { return }
        polling = true
        queue.async {
            var found: [UUID: Lookup] = [:]
            for session in sessions { found[session.id] = Self.lookup(in: session.dir) }
            DispatchQueue.main.async {
                self.polling = false
                self.apply(found)
            }
        }
    }

    private func apply(_ found: [UUID: Lookup]) {
        var result: [UUID: PullRequest] = [:]
        for (id, lookup) in found {
            switch lookup {
            case .found(let pr):
                if let old = requests[id], pr.needsAttention(since: old) { Store.shared.requestAttention(id) }
                result[id] = pr
            case .none:
                break
            case .unknown:
                // Offline or rate limited: keep what we knew.
                result[id] = requests[id]
            }
        }
        if result != requests { requests = result }
    }

    nonisolated private static let fields = "number,title,url,state,isDraft,reviewDecision,statusCheckRollup"

    /// Asks about the branch checked out in `dir` rather than naming it, so gh can follow
    /// the branch's upstream to a pull request opened from a fork.
    nonisolated private static func lookup(in dir: String) -> Lookup {
        guard let result = Git.gh(["pr", "view", "--json", fields], in: dir) else { return .none }
        guard result.ok else {
            return result.err.contains("no pull requests found") ? .none : .unknown
        }
        guard let raw = try? JSONDecoder().decode(Raw.self, from: Data(result.out.utf8)),
              let url = URL(string: raw.url), let state = PullRequest.State(rawValue: raw.state) else { return .unknown }
        let checks = (raw.statusCheckRollup ?? []).enumerated().map { index, check in
            PullRequest.Check(id: index, name: check.name ?? check.context ?? "Check",
                              result: check.result, url: (check.detailsUrl ?? check.targetUrl).flatMap(URL.init(string:)))
        }
        return .found(PullRequest(number: raw.number, title: raw.title, url: url, state: state, isDraft: raw.isDraft,
                                  review: raw.reviewDecision.flatMap(PullRequest.Review.init(rawValue:)), checks: checks))
    }

    /// `gh pr view --json` output. Checks come as CheckRuns (status and conclusion) or
    /// as commit StatusContexts (state).
    private struct Raw: Decodable {
        struct Check: Decodable {
            let name: String?
            let context: String?
            let status: String?
            let conclusion: String?
            let state: String?
            let detailsUrl: String?
            let targetUrl: String?

            var result: PullRequest.Result {
                if let state {
                    switch state {
                    case "SUCCESS": return .passed
                    case "PENDING", "EXPECTED": return .pending
                    default: return .failed
                    }
                }
                guard status == "COMPLETED" else { return .pending }
                switch conclusion {
                case "SUCCESS": return .passed
                case "SKIPPED", "NEUTRAL", "STALE": return .skipped
                default: return .failed
                }
            }
        }

        let number: Int
        let title: String
        let url: String
        let state: String
        let isDraft: Bool
        let reviewDecision: String?
        let statusCheckRollup: [Check]?
    }
}

// MARK: - Views

/// "● #42" after a worktree session's branch in the sidebar.
struct PullRequestBadge: View {
    let pr: PullRequest

    var body: some View {
        HStack(spacing: 3) {
            Circle().fill(pr.color).frame(width: 5, height: 5)
            Text("#\(pr.number)")
        }
        .fixedSize()
        .help("#\(pr.number) · \(pr.summary)")
    }
}

/// "#42 · Checks failing" in the header strip; opens the pull request's checks.
struct PullRequestChip: View {
    @ObservedObject private var monitor = PullRequestMonitor.shared
    let tabID: UUID
    @State private var open = false

    var body: some View {
        if let pr = monitor.requests[tabID] {
            Button { open.toggle() } label: {
                HStack(spacing: 5) {
                    Image(systemName: "arrow.triangle.pull")
                    Text("#\(pr.number)")
                    Circle().fill(pr.color).frame(width: 6, height: 6)
                    Text(pr.summary)
                }
                .padding(.horizontal, 7)
                .frame(height: 18)
                .background(open ? Color.primary.opacity(0.1) : .clear, in: Capsule())
                .overlay(Capsule().strokeBorder(.primary.opacity(0.15)))
                .contentShape(Capsule())
            }
            .buttonStyle(.plain)
            .fixedSize()
            .help("Pull request #\(pr.number): \(pr.title)")
            .popover(isPresented: $open, arrowEdge: .bottom) {
                PullRequestDetails(pr: pr) { open = false }
            }
        }
    }
}

struct PullRequestDetails: View {
    let pr: PullRequest
    let dismiss: () -> Void

    private var facts: String {
        var facts = ["#\(pr.number)", pr.isDraft && pr.state == .open ? "Draft" : pr.state.rawValue.capitalized]
        switch pr.review {
        case .approved: facts.append("Approved")
        case .changesRequested: facts.append("Changes requested")
        case .required: facts.append("Review required")
        case nil: break
        }
        return facts.joined(separator: " · ")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            VStack(alignment: .leading, spacing: 2) {
                Text(pr.title).font(.system(size: 12, weight: .semibold)).lineLimit(2)
                Text(facts).font(.caption).foregroundStyle(.secondary)
            }
            .padding(.horizontal, 8)
            .padding(.bottom, 6)
            if pr.checks.isEmpty {
                Text("No checks").font(.caption).foregroundStyle(.secondary).padding(.horizontal, 8)
            } else {
                ScrollView {
                    VStack(spacing: 2) {
                        ForEach(pr.checks) { row($0) }
                    }
                }
                .frame(maxHeight: 260)
            }
            Divider().padding(.vertical, 4)
            Button {
                NSWorkspace.shared.open(pr.url)
                dismiss()
            } label: {
                Label("Open on GitHub", systemImage: "arrow.up.right.square")
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 8)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
        }
        .font(.system(size: 11))
        .padding(.vertical, 8)
        .padding(.horizontal, 4)
        .frame(width: 320)
    }

    private func row(_ check: PullRequest.Check) -> some View {
        Button {
            if let url = check.url { NSWorkspace.shared.open(url) }
        } label: {
            HStack(spacing: 6) {
                Circle().fill(color(check.result)).frame(width: 6, height: 6)
                Text(check.name).lineLimit(1)
                Spacer(minLength: 4)
                Text(label(check.result)).font(.caption).foregroundStyle(.secondary)
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 2)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(check.url == nil)
    }

    private func color(_ result: PullRequest.Result) -> Color {
        switch result {
        case .passed: return .green
        case .failed: return .red
        case .pending: return .blue
        case .skipped: return .secondary
        }
    }

    private func label(_ result: PullRequest.Result) -> String {
        switch result {
        case .passed: return "passed"
        case .failed: return "failed"
        case .pending: return "running"
        case .skipped: return "skipped"
        }
    }
}
