import AppKit
import UserNotifications

/// macOS notifications for sessions you aren't looking at. Clicking one opens its session.
/// Only works when omux runs as an app bundle: the notification center traps in a bare
/// binary (`swift run`), so there it does nothing.
@MainActor
final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    static let shared = Notifier()

    private let center: UNUserNotificationCenter? =
        Bundle.main.bundleURL.pathExtension == "app" ? .current() : nil

    /// Call before launch finishes, so a click on a notification reaches us.
    func activate() {
        center?.delegate = self
    }

    /// Shows a notification for a session, replacing the one it showed before.
    func post(tabID: UUID, title: String, subtitle: String?, body: String) {
        guard let center else { return }
        let content = UNMutableNotificationContent()
        content.title = title
        if let subtitle, !subtitle.isEmpty { content.subtitle = subtitle }
        content.body = body
        content.sound = .default
        content.threadIdentifier = tabID.uuidString
        content.userInfo = ["tabID": tabID.uuidString]
        let request = UNNotificationRequest(identifier: tabID.uuidString, content: content, trigger: nil)
        Task {
            guard await isAuthorized(center) else { return }
            try? await center.add(request)
        }
    }

    /// Removes a session's notification from Notification Center once you've seen the session.
    func clear(tabID: UUID) {
        center?.removeDeliveredNotifications(withIdentifiers: [tabID.uuidString])
    }

    /// Asks for permission the first time there is something to show, not at launch.
    private func isAuthorized(_ center: UNUserNotificationCenter) async -> Bool {
        switch await center.notificationSettings().authorizationStatus {
        case .authorized, .provisional: return true
        case .notDetermined: return (try? await center.requestAuthorization(options: [.alert, .sound])) ?? false
        default: return false
        }
    }

    // MARK: UNUserNotificationCenterDelegate

    /// omux only posts for sessions you aren't watching, so show them even while it's in front.
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .list, .sound]
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        guard let raw = response.notification.request.content.userInfo["tabID"] as? String,
              let id = UUID(uuidString: raw) else { return }
        await MainActor.run { Store.shared.reveal(id) }
    }
}
