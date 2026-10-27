import AppKit
import SwiftUI

@main
struct OmuxApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var store = Store.shared

    var body: some Scene {
        Window("omux", id: "main") {
            ContentView()
                .environmentObject(store)
                .frame(minWidth: 720, minHeight: 420)
        }
        .defaultSize(width: 1240, height: 780)
        .windowStyle(.hiddenTitleBar)
        .commands { AppCommands(store: store) }

        Settings { SettingsView() }
    }
}

enum Prefs {
    static let confirmQuit = "confirmQuit"
    static let showFolderPath = "showFolderPath"
    static let usageInterval = "usageInterval"
    static let theme = "theme"
    static let autoResume = "autoResume"
    static let showFiles = "showFiles"
    static let filesByDate = "filesByDate"
    static let filesBrowserCollapsed = "filesBrowserCollapsed"
    static let previewWrapLines = "previewWrapLines"
    static let fontFamily = "fontFamily"
    static let fontSize = "fontSize"
}

struct SettingsView: View {
    @AppStorage(Prefs.confirmQuit) private var confirmQuit = true
    @AppStorage(Prefs.showFolderPath) private var showFolderPath = true
    @AppStorage(Prefs.usageInterval) private var usageInterval = 300.0
    @AppStorage(Prefs.theme) private var theme = Theme.dark.rawValue
    @AppStorage(Prefs.fontFamily) private var fontFamily = ""
    @AppStorage(Prefs.fontSize) private var fontSize = 13.0
    @AppStorage(Prefs.autoResume) private var autoResume = false
    @AppStorage(Prefs.previewWrapLines) private var wrapLines = true

    var body: some View {
        Form {
            Section("Appearance") {
                Picker("Theme", selection: $theme) {
                    ForEach(Theme.allCases) { Text($0.label).tag($0.rawValue) }
                }
                Picker("Font", selection: $fontFamily) {
                    Text("System Mono").tag("")
                    ForEach(Fonts.monospaceFamilies, id: \.self) { Text($0).tag($0) }
                }
                Stepper("Font size: \(Int(fontSize)) pt", value: $fontSize, in: 9 ... 24)
            }
            Section("Sidebar") {
                Toggle("Show folder path under session name", isOn: $showFolderPath)
                Picker("Check Claude limits every", selection: $usageInterval) {
                    Text("1 minute").tag(60.0)
                    Text("5 minutes").tag(300.0)
                    Text("30 minutes").tag(1800.0)
                    Text("Hour").tag(3600.0)
                }
            }
            Section("Files Panel") {
                Toggle("Wrap long lines in file preview", isOn: $wrapLines)
            }
            Section("Sessions") {
                Toggle("Auto-resume Claude sessions at launch", isOn: $autoResume)
                Text("Off: restored sessions stay stopped until you press ▶. Clicking one only opens its shell.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Toggle("Confirm before quitting while sessions are running", isOn: $confirmQuit)
                Text("Quitting always stops every session. Claude conversations resume on the next launch.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(width: 460)
    }
}

struct ContentView: View {
    @EnvironmentObject var store: Store
    @Environment(\.openWindow) private var openWindow
    @AppStorage(Prefs.theme) private var themeName = Theme.dark.rawValue
    @AppStorage(Prefs.fontFamily) private var fontFamily = ""
    @AppStorage(Prefs.fontSize) private var fontSize = 13.0
    @AppStorage(Prefs.showFiles) private var showFiles = false

    private var theme: Theme { Theme(rawValue: themeName) ?? .dark }

    /// Folder the file panel shows: the selected session's folder, else its project's.
    private var projectRoot: URL {
        let tab = store.selectedTab
        let group = store.selectedGroup
        let path = tab?.directory ?? group?.directory ?? NSHomeDirectory()
        return URL(fileURLWithPath: (path as NSString).expandingTildeInPath)
    }

    var body: some View {
        NavigationSplitView {
            Sidebar(theme: theme)
                .toolbar(removing: .sidebarToggle)
        } detail: {
            HSplitView {
                VStack(spacing: 0) {
                    SessionHeader(theme: theme, showFiles: $showFiles)
                    if let id = store.selectedTabID, let term = store.terminal(for: id) {
                        TerminalHost(termView: term, theme: theme)
                    } else {
                        ContentUnavailableView("No session", systemImage: "terminal", description: Text("Press ⌘T to start one."))
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                            .background(Color(nsColor: theme.background))
                    }
                }
                .frame(minWidth: 360)
                if showFiles {
                    FilePanel(root: projectRoot, theme: theme)
                        .frame(minWidth: 240, idealWidth: 340, maxWidth: 800)
                }
            }
            .ignoresSafeArea(.container, edges: .top)
        }
        .toolbar(.hidden, for: .windowToolbar)
        .preferredColorScheme(theme.colorScheme)
        .background(Color(nsColor: theme.chrome))
        .onChange(of: themeName) { _, _ in store.applyAppearance() }
        .onChange(of: fontFamily) { _, _ in store.applyAppearance() }
        .onChange(of: fontSize) { _, _ in store.applyAppearance() }
        .onChange(of: store.previewRequest) { _, path in if path != nil { showFiles = true } }
        .sheet(isPresented: $store.showQuickSwitcher) { QuickSwitcher().environmentObject(store) }
        .sheet(isPresented: $store.showRecentSessions) { RecentSessionsSheet().environmentObject(store) }
        .sheet(item: $store.editingTabID) { TabEditSheet(tabID: $0).environmentObject(store) }
        .sheet(item: $store.editingGroupID) { GroupEditSheet(groupID: $0).environmentObject(store) }
        .onAppear { AppDelegate.openWindow = openWindow }
    }
}

/// Slim strip above the terminal: name, live status, folder and stop/start.
struct SessionHeader: View {
    @EnvironmentObject var store: Store
    let theme: Theme
    @Binding var showFiles: Bool

    var body: some View {
        HStack(spacing: 10) {
            if let tab = store.selectedTab {
                let rt = store.runtime[tab.id] ?? TabRuntime()
                Circle()
                    .fill(rt.statusColor ?? tab.color.color ?? Color.secondary.opacity(0.4))
                    .frame(width: 8, height: 8)
                Text(tab.name)
                    .font(.system(size: 11, weight: .semibold))
                    .lineLimit(1)
                if let line = rt.statusLine(showFolder: false) {
                    Text(line)
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                Spacer()
                if let cwd = rt.cwd {
                    Text((cwd as NSString).abbreviatingWithTildeInPath)
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                        .lineLimit(1)
                        .truncationMode(.head)
                }
                if rt.busy {
                    Button { store.stop(tab.id) } label: { Image(systemName: "stop.fill") }
                        .help("Interrupt (Ctrl-C)")
                } else {
                    Button { store.start(tab.id) } label: { Image(systemName: "play.fill") }
                        .help("Continue this Claude conversation")
                }
            } else {
                Spacer()
            }
            Button { showFiles.toggle() } label: { Image(systemName: "sidebar.trailing") }
                .foregroundStyle(showFiles ? .primary : .secondary)
                .help("Files panel (⌥⌘F)")
        }
        .buttonStyle(.plain)
        .foregroundStyle(.secondary)
        .font(.system(size: 11))
        .padding(.horizontal, 10)
        .frame(height: 28)
        .background(Color(nsColor: theme.chrome))
        .overlay(alignment: .bottom) { Rectangle().fill(.primary.opacity(0.08)).frame(height: 1) }
    }
}

struct AppCommands: Commands {
    @ObservedObject var store: Store

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button("New Session") { store.newTab() }.keyboardShortcut("t")
            Button("New Shell") { store.newTab(command: "") }.keyboardShortcut("t", modifiers: [.command, .shift])
            Button("New Project") { store.newGroup() }.keyboardShortcut("n", modifiers: [.command, .shift])
            Divider()
            Button("Open Folder…") { store.openFolderPanel() }.keyboardShortcut("o")
            Button("Open Recent Claude Session…") { store.showRecentSessions = true }
                .keyboardShortcut("o", modifiers: [.command, .shift])
        }
        CommandGroup(replacing: .saveItem) {
            Button("Close Session") { if let id = store.selectedTabID { store.closeTab(id) } }
                .keyboardShortcut("w")
            Button("Close Window") { NSApp.keyWindow?.performClose(nil) }
                .keyboardShortcut("w", modifiers: [.command, .shift])
        }
        CommandGroup(after: .sidebar) {
            Button("Files Panel") {
                UserDefaults.standard.set(!UserDefaults.standard.bool(forKey: Prefs.showFiles), forKey: Prefs.showFiles)
            }
            .keyboardShortcut("f", modifiers: [.command, .option])
        }
        CommandMenu("Session") {
            Button("Rename") { store.renamingID = store.selectedTabID }.keyboardShortcut("r")
            Button("Edit Session…") { store.editingTabID = store.selectedTabID }.keyboardShortcut("i")
            Button("Restart Session") { if let id = store.selectedTabID { store.restart(id) } }
                .keyboardShortcut("r", modifiers: [.command, .shift])
            Button("Interrupt (Ctrl-C)") { if let id = store.selectedTabID { store.stop(id) } }
                .keyboardShortcut(".", modifiers: [.command, .shift])
            Button("Kill Session") { if let id = store.selectedTabID { store.kill(id) } }
            Button("Continue Claude Conversation") { if let id = store.selectedTabID { store.start(id) } }
                .keyboardShortcut(.return, modifiers: [.command, .shift])
            Divider()
            Button("Jump to Session…") { store.showQuickSwitcher = true }.keyboardShortcut("k")
            Button("Next Session") { store.selectRelative(1) }.keyboardShortcut("]", modifiers: [.command, .shift])
            Button("Previous Session") { store.selectRelative(-1) }.keyboardShortcut("[", modifiers: [.command, .shift])
            Divider()
            ForEach(1 ..< 10, id: \.self) { n in
                Button("Session \(n)") { store.selectIndex(n - 1) }
                    .keyboardShortcut(KeyEquivalent(Character(String(n))))
            }
        }
    }
}

/// Finder's right-click "Open in omux" on folders (declared under NSServices in Info.plist).
final class ServiceProvider: NSObject {
    @objc func openFolders(_ pboard: NSPasteboard, userData: String, error: AutoreleasingUnsafeMutablePointer<NSString>) {
        let urls = pboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
        DispatchQueue.main.async {
            NSApp.activate(ignoringOtherApps: true)
            AppDelegate.openWindow?(id: "main")
            urls.forEach { Store.shared.openFolder($0.path) }
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    @MainActor static var openWindow: OpenWindowAction?
    private let services = ServiceProvider()

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.servicesProvider = services
        NSUpdateDynamicServices()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    /// Folders dropped on the Dock icon, opened via Finder's "Open With", or `open -a omux <dir>`.
    func application(_ application: NSApplication, open urls: [URL]) {
        Self.openWindow?(id: "main")
        urls.forEach { Store.shared.openFolder($0.path) }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        if !hasVisibleWindows { Self.openWindow?(id: "main") }
        return true
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        let store = Store.shared
        let running = store.runningSessionCount
        let confirm = UserDefaults.standard.object(forKey: Prefs.confirmQuit) as? Bool ?? true
        if confirm, running > 0 {
            let alert = NSAlert()
            alert.messageText = "Quit omux?"
            alert.informativeText = "\(running) session\(running == 1 ? " is" : "s are") running. Claude sessions resume automatically on next launch."
            alert.addButton(withTitle: "Quit")
            alert.addButton(withTitle: "Cancel")
            if alert.runModal() != .alertFirstButtonReturn { return .terminateCancel }
        }
        store.prepareForTermination()
        return .terminateNow
    }
}
