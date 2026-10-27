using System.IO;
using System.Text;
using System.Text.Json;

namespace Omux;

/// <summary>A Claude Code conversation found in ~/.claude/projects.</summary>
public sealed record RecentSession(Guid Id, string Cwd, string Prompt, DateTime Date)
{
    public static string ProjectsDirectory => Path.Combine(Paths.Home, ".claude", "projects");

    public string Folder => Paths.Abbreviate(Cwd);

    /// <summary>Short name for a tab: first words of the first prompt.</summary>
    public string TabName
    {
        get
        {
            var words = string.Join(' ', Prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(5));
            return words.Length > 32 ? words[..32] + "…" : words;
        }
    }

    /// <summary>Claude Code writes each conversation to ~/.claude/projects/&lt;project&gt;/&lt;session&gt;.jsonl.</summary>
    public static bool Exists(string session)
    {
        try
        {
            return Directory.EnumerateDirectories(ProjectsDirectory)
                .Any(dir => File.Exists(Path.Combine(dir, session + ".jsonl")));
        }
        catch { return false; }
    }

    /// <summary>Scans transcripts, newest first. Reads only the head of each file.</summary>
    public static List<RecentSession> Load(int limit = 300)
    {
        var files = new List<FileInfo>();
        try
        {
            foreach (var project in new DirectoryInfo(ProjectsDirectory).EnumerateDirectories())
                files.AddRange(project.EnumerateFiles("*.jsonl"));
        }
        catch { return []; }

        var result = new List<RecentSession>();
        foreach (var file in files.OrderByDescending(f => f.LastWriteTime).Take(limit))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file.Name), out var id)) continue;
            string head;
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[128 * 1024];
                var read = stream.Read(buffer, 0, buffer.Length);
                head = Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch { continue; }

            foreach (var line in head.Split('\n'))
            {
                if (FirstPrompt(line) is not { } found) continue;
                result.Add(new RecentSession(id, found.Cwd, found.Prompt.Replace("\r", "").Replace("\n", " "), file.LastWriteTime));
                break;
            }
        }
        return result;
    }

    private static (string Cwd, string Prompt)? FirstPrompt(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var record = doc.RootElement;
            if (record.GetProperty("type").GetString() != "user") return null;
            if (!record.TryGetProperty("cwd", out var cwd) || cwd.GetString() is not { } folder) return null;
            if (!record.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)) return null;
            var text = content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : string.Join(' ', content.EnumerateArray()
                    .Where(b => b.TryGetProperty("text", out _))
                    .Select(b => b.GetProperty("text").GetString()));
            var prompt = text?.Trim();
            // Skip slash-command and system messages.
            if (string.IsNullOrEmpty(prompt) || prompt.StartsWith('<')) return null;
            return (folder, prompt);
        }
        catch { return null; }
    }
}
