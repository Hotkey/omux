import SwiftUI

enum PanelTab: String, CaseIterable {
    case files, changes, browser
}

/// The right panel: Files, Changes and Browser for the selected session.
struct RightPanel: View {
    @EnvironmentObject var store: Store
    @ObservedObject private var changes = Changes.shared
    let root: URL
    let theme: Theme
    @AppStorage(Prefs.panelTab) private var tab = PanelTab.files.rawValue

    private var current: PanelTab { PanelTab(rawValue: tab) ?? .files }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 2) {
                button(.files, "Files", "doc", shortcut: "1")
                button(.changes, "Changes", "plusminus", shortcut: "2",
                       badge: changes.tabID == store.selectedTabID ? changes.files.count : 0)
                button(.browser, "Browser", "globe", shortcut: "3")
                Spacer()
            }
            .padding(.horizontal, 6)
            .frame(height: 28)
            .background(Color(nsColor: theme.chrome))
            .overlay(alignment: .bottom) { Rectangle().fill(.primary.opacity(0.08)).frame(height: 1) }

            ZStack {
                // Files stays alive so its preview and scroll position survive tab switches.
                FilePanel(root: root, theme: theme)
                    .opacity(current == .files ? 1 : 0)
                    .allowsHitTesting(current == .files)
                    .accessibilityHidden(current != .files)
                if current == .changes {
                    ChangesPanel(theme: theme)
                }
                if current == .browser {
                    if let id = store.selectedTabID {
                        BrowserPanel(session: store.browser(for: id), theme: theme).id(id)
                    } else {
                        Text("No session").foregroundStyle(.secondary)
                    }
                }
            }
        }
    }

    private func button(_ value: PanelTab, _ title: String, _ icon: String, shortcut: String, badge: Int = 0) -> some View {
        let selected = current == value
        return Button { tab = value.rawValue } label: {
            HStack(spacing: 5) {
                Image(systemName: icon).imageScale(.small)
                Text(title)
                if badge > 0 {
                    Text("\(badge)")
                        .font(.system(size: 9, weight: .semibold))
                        .padding(.horizontal, 4)
                        .frame(minWidth: 15, minHeight: 14)
                        .background(.primary.opacity(0.14), in: Capsule())
                }
            }
            .font(.system(size: 11, weight: .medium))
            .foregroundStyle(selected ? .primary : .secondary)
            .padding(.horizontal, 8)
            .frame(height: 22)
            .background(selected ? Color.primary.opacity(0.1) : .clear, in: RoundedRectangle(cornerRadius: 5))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help("\(title) (⌥⌘\(shortcut))")
    }
}
