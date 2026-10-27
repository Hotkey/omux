import Foundation

/// A Claude Code conversation found in ~/.claude/projects.
struct RecentSession: Identifiable, Hashable {
    let id: UUID
    let cwd: String
    let prompt: String
    let date: Date

    var folder: String { (cwd as NSString).abbreviatingWithTildeInPath }

    /// Short name for a tab: first words of the first prompt.
    var tabName: String {
        let words = prompt.split(separator: " ").prefix(5).joined(separator: " ")
        return words.count > 32 ? String(words.prefix(32)) + "…" : words
    }

    private struct Record: Decodable {
        struct Message: Decodable {
            let content: Content?
            enum Content: Decodable {
                case text(String), blocks([Block])
                struct Block: Decodable { let text: String? }
                init(from decoder: Decoder) throws {
                    let c = try decoder.singleValueContainer()
                    if let s = try? c.decode(String.self) { self = .text(s) } else { self = .blocks(try c.decode([Block].self)) }
                }
                var text: String {
                    switch self {
                    case .text(let s): return s
                    case .blocks(let b): return b.compactMap(\.text).joined(separator: " ")
                    }
                }
            }
        }
        let type: String
        let cwd: String?
        let message: Message?
    }

    /// Scans transcripts, newest first. Reads only the head of each file.
    static func load(limit: Int = 300) -> [RecentSession] {
        let root = URL(fileURLWithPath: NSHomeDirectory() + "/.claude/projects")
        let fm = FileManager.default
        guard let projects = try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: nil) else { return [] }
        var files: [(URL, Date)] = []
        for project in projects {
            guard let items = try? fm.contentsOfDirectory(at: project, includingPropertiesForKeys: [.contentModificationDateKey]) else { continue }
            for url in items where url.pathExtension == "jsonl" {
                let date = (try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
                files.append((url, date))
            }
        }
        files.sort { $0.1 > $1.1 }
        let decoder = JSONDecoder()
        var result: [RecentSession] = []
        for (url, date) in files.prefix(limit) {
            guard let id = UUID(uuidString: url.deletingPathExtension().lastPathComponent),
                  let handle = try? FileHandle(forReadingFrom: url) else { continue }
            let head = handle.readData(ofLength: 128 * 1024)
            try? handle.close()
            guard let text = String(data: head, encoding: .utf8) else { continue }
            for line in text.split(separator: "\n") {
                guard let record = try? decoder.decode(Record.self, from: Data(line.utf8)),
                      record.type == "user",
                      let cwd = record.cwd,
                      let prompt = record.message?.content?.text.trimmingCharacters(in: .whitespacesAndNewlines),
                      !prompt.isEmpty, !prompt.hasPrefix("<") // skip slash-command and system messages
                else { continue }
                result.append(RecentSession(id: id, cwd: cwd, prompt: prompt.replacingOccurrences(of: "\n", with: " "), date: date))
                break
            }
        }
        return result
    }
}
