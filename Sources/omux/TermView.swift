import AppKit
import SwiftTerm
import SwiftUI

enum TermEvent {
    case bell
    case title(String)
    case directory(String?)
    case exited
    /// OSC 7501: the root record's state, kind and message, plus every other record.
    case status(TabRuntime.StatusState?, kind: String?, message: String?, children: [TabRuntime.StatusChild])
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
        let children = records.filter { !$0.id.isEmpty }.map {
            TabRuntime.StatusChild(id: $0.id, state: TabRuntime.StatusState(rawValue: $0.state.rawValue),
                                   kind: $0.kind?.rawValue, message: $0.message, title: $0.title)
        }
        guard let root = records.first(where: { $0.id.isEmpty }) ?? records.last else {
            owner?.emit(.status(nil, kind: nil, message: nil, children: []))
            return
        }
        let state = TabRuntime.StatusState(rawValue: root.state.rawValue)
        owner?.emit(.status(state, kind: root.kind?.rawValue, message: root.message, children: children))
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

    nonisolated static func filePath(from link: String) -> String? {
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

    /// Types text into the session as a paste, so newlines don't submit it early.
    func paste(_ text: String) {
        if terminalInputStateSnapshot()?.bracketedPasteMode == true {
            send(txt: "\u{1b}[200~" + text + "\u{1b}[201~")
        } else {
            send(txt: text)
        }
    }

    /// The main buffer's text, scrollback included. Read it here on the main actor;
    /// the parsers below are safe to run on the copy from any thread.
    func scrollbackText() -> String {
        String(data: getBufferAsData(kind: .normal), encoding: .utf8) ?? ""
    }

    /// Local server URLs (localhost, 127.0.0.1, …) in `text`, oldest first.
    nonisolated static func mentionedLocalURLs(in text: String) -> [String] {
        let regex = try! NSRegularExpression(pattern: #"https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\])(?::\d+)?(?:/[^\s'"<>)\]]*)?"#)
        var result: [String] = []
        for match in regex.matches(in: text, range: NSRange(text.startIndex..., in: text)) {
            guard let range = Range(match.range, in: text) else { continue }
            var url = String(text[range])
            while let last = url.last, ".,:;".contains(last) { url.removeLast() }
            result.removeAll { $0 == url }
            result.append(url)
        }
        return result
    }

    /// File paths that appear in `text` and still exist, oldest first.
    nonisolated static func mentionedPaths(in text: String) -> [String] {
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
