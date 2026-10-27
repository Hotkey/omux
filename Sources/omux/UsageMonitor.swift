import Foundation
import SwiftUI

/// Reads Claude Code's OAuth token from the Keychain and polls the same usage
/// endpoint Claude Code's `/usage` command uses.
@MainActor
final class UsageMonitor: ObservableObject {
    struct Limit: Identifiable {
        let id: String
        let label: String
        let percent: Int
        let resetsAt: Date?

        var tint: Color {
            percent >= 90 ? .red : percent >= 70 ? .orange : .accentColor
        }

        var resetText: String {
            guard let resetsAt else { return label }
            let seconds = max(0, Int(resetsAt.timeIntervalSinceNow))
            let h = seconds / 3600, m = (seconds % 3600) / 60
            let when = seconds >= 86400 ? "\(seconds / 86400)d \(h % 24)h" : h > 0 ? "\(h)h \(m)m" : "\(m)m"
            return "\(label) resets in \(when)"
        }
    }

    @Published private(set) var limits: [Limit] = []
    @Published private(set) var error: String?

    private var timer: Timer?

    /// Polls at the interval chosen in Settings (seconds); re-arms when it changes.
    func start() {
        let interval = UserDefaults.standard.double(forKey: Prefs.usageInterval)
        schedule(every: interval > 0 ? interval : 300)
        refresh()
    }

    func schedule(every seconds: TimeInterval) {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: seconds, repeats: true) { _ in
            DispatchQueue.main.async { [weak self] in self?.refresh() }
        }
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    func refresh() {
        Task { await load() }
    }

    private struct Response: Decodable {
        struct Entry: Decodable {
            struct Scope: Decodable {
                struct Model: Decodable { let display_name: String? }
                let model: Model?
            }
            let kind: String
            let percent: Int
            let resets_at: String?
            let scope: Scope?
        }
        let limits: [Entry]
    }

    private func load() async {
        guard let token = await Task.detached(operation: Self.readToken).value else {
            error = "Not signed in to Claude Code"
            return
        }
        var request = URLRequest(url: URL(string: "https://api.anthropic.com/api/oauth/usage")!)
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
        request.timeoutInterval = 15
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            guard (response as? HTTPURLResponse)?.statusCode == 200 else {
                error = "Usage unavailable"
                return
            }
            let decoded = try JSONDecoder().decode(Response.self, from: data)
            let iso = ISO8601DateFormatter()
            iso.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            limits = decoded.limits.map { entry in
                let label: String
                switch entry.kind {
                case "session": label = "5h"
                case "weekly_all": label = "Week"
                default: label = entry.scope?.model?.display_name ?? entry.kind
                }
                return Limit(id: entry.kind + (entry.scope?.model?.display_name ?? ""),
                             label: label,
                             percent: entry.percent,
                             resetsAt: entry.resets_at.flatMap { iso.date(from: $0) })
            }
            error = nil
        } catch {
            self.error = "Usage unavailable"
        }
    }

    /// Reads the token the way Claude Code stores it, through /usr/bin/security, which is
    /// already allowed on that Keychain item, so macOS never prompts for omux itself.
    private static func readToken() -> String? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/security")
        process.arguments = ["find-generic-password", "-s", "Claude Code-credentials", "-w"]
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice
        guard (try? process.run()) != nil else { return nil }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let oauth = json["claudeAiOauth"] as? [String: Any],
              let token = oauth["accessToken"] as? String
        else { return nil }
        return token
    }
}
