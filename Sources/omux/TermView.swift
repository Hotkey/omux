import AppKit
import SwiftTerm
import SwiftUI

enum TermEvent {
    case bell
    case title(String)
    case directory(String?)
    case exited
    case status(TabRuntime.StatusState?, String?)
    /// ⌘-click on a file path in the output.
    case openPath(String)
}

/// Receives process callbacks on behalf of a TermView. Kept separate because
/// LocalProcessTerminalView already declares methods with these signatures.
private final class ProcessEvents: LocalProcessTerminalViewDelegate {
    weak var owner: TermView?

    func sizeChanged(source: LocalProcessTerminalView, newCols: Int, newRows: Int) {}

    func setTerminalTitle(source: LocalProcessTerminalView, title: String) {
        owner?.emit(.title(title))
    }

    func hostCurrentDirectoryUpdate(source: TerminalView, directory: String?) {
        // OSC 7 sends a file:// URL
        let path = directory.flatMap { URL(string: $0)?.path ?? $0 }
        owner?.emit(.directory(path))
    }

    func processTerminated(source: TerminalView, exitCode: Int32?) {
        owner?.emit(.exited)
    }

    func processFailedToStart(source: TerminalView, error: LocalProcessError) {
        owner?.emit(.exited)
    }

    func programStatusChanged(source: TerminalView, records: [TerminalProgramStatus]) {
        guard let root = records.first(where: { $0.id.isEmpty }) ?? records.last else {
            owner?.emit(.status(nil, nil))
            return
        }
        let state = TabRuntime.StatusState(rawValue: root.state.rawValue)
        owner?.emit(.status(state, root.kind?.rawValue ?? root.message))
    }
}

final class TermView: LocalProcessTerminalView {
    let tabID: UUID
    var onEvent: ((TermEvent) -> Void)?
    private let events = ProcessEvents()

    init(tabID: UUID) {
        self.tabID = tabID
        super.init(frame: NSRect(x: 0, y: 0, width: 800, height: 500),
                   font: Fonts.terminal(),
                   options: TerminalOptions(scrollback: 5000))
        events.owner = self
        processDelegate = events
        apply(Theme.current)
    }

    required init?(coder: NSCoder) { fatalError("not supported") }

    func apply(_ theme: Theme) {
        font = Fonts.terminal()
        nativeBackgroundColor = theme.background
        nativeForegroundColor = theme.foreground
        caretColor = theme.foreground
        installColors(theme.palette)
    }

    fileprivate func emit(_ event: TermEvent) {
        if Thread.isMainThread {
            onEvent?(event)
        } else {
            DispatchQueue.main.async { self.onEvent?(event) }
        }
    }

    /// ⌘-click: file paths go to the preview panel, everything else (URLs) to the system.
    override func requestOpenLink(source: TerminalView, link: String, params: [String: String]) {
        if let path = Self.filePath(from: link) {
            emit(.openPath(path))
        } else {
            super.requestOpenLink(source: source, link: link, params: params)
        }
    }

    static func filePath(from link: String) -> String? {
        var raw = link.trimmingCharacters(in: .whitespaces)
        if raw.hasPrefix("file://"), let url = URL(string: raw) { raw = url.path }
        guard raw.hasPrefix("/") || raw.hasPrefix("~") else { return nil }
        // Drop trailing punctuation Claude's prose leaves on paths.
        while let last = raw.last, ".,:;)]}'\"".contains(last) { raw.removeLast() }
        let path = (raw as NSString).expandingTildeInPath
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: path, isDirectory: &isDir), !isDir.boolValue else { return nil }
        return path
    }

    /// File paths that appear in the scrollback and still exist, oldest first.
    func mentionedPaths() -> [String] {
        let data = getBufferAsData(kind: .normal)
        guard let text = String(data: data, encoding: .utf8) else { return [] }
        let regex = try! NSRegularExpression(pattern: #"(?<![\w])(?:~|/)[\w.+@%~/-]+"#)
        var seen = Set<String>()
        var result: [String] = []
        for match in regex.matches(in: text, range: NSRange(text.startIndex..., in: text)) {
            guard let range = Range(match.range, in: text), let path = Self.filePath(from: String(text[range])) else { continue }
            if seen.insert(path).inserted { result.append(path) } else if let i = result.firstIndex(of: path) {
                result.remove(at: i); result.append(path)
            }
        }
        return result
    }

    override nonisolated func bell(source: Terminal) {
        super.bell(source: source)
        DispatchQueue.main.async {
            NSSound.beep()
            self.onEvent?(.bell)
        }
    }
}

/// Hosts the long-lived TermView for the selected tab. Terminal views are owned by
/// the Store so switching tabs reparents a view instead of recreating the session.
struct TerminalHost: NSViewRepresentable {
    let termView: TermView
    let theme: Theme

    final class Coordinator { var currentID: UUID? }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> NSView {
        let container = NSView()
        container.wantsLayer = true
        return container
    }

    func updateNSView(_ container: NSView, context: Context) {
        container.layer?.backgroundColor = theme.background.cgColor
        guard context.coordinator.currentID != termView.tabID || termView.superview !== container else { return }
        context.coordinator.currentID = termView.tabID
        container.subviews.forEach { $0.removeFromSuperview() }
        termView.frame = container.bounds
        termView.autoresizingMask = [.width, .height]
        container.addSubview(termView)
        DispatchQueue.main.async {
            termView.window?.makeFirstResponder(termView)
        }
    }
}
