using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Omux;

/// <summary>
/// A Claude Code subagent of one session. Claude Code writes each one to
/// ~/.claude/projects/&lt;project&gt;/&lt;session&gt;/subagents/agent-&lt;id&gt;.meta.json, with its
/// transcript next to it as agent-&lt;id&gt;.jsonl.
/// </summary>
public sealed record SubAgent(string Id, string Type, string Description, bool Background, int Depth, bool Finished,
    DateTime Started, DateTime? Ended, string Transcript, string? Activity, int ToolCount, bool Blocked = false)
{
    public string ElapsedText
    {
        get
        {
            var seconds = (int)Math.Max(0, ((Ended ?? DateTime.Now) - Started).TotalSeconds);
            return seconds >= 3600 ? $"{seconds / 3600}h {seconds % 3600 / 60}m" : $"{seconds / 60}:{seconds % 60:00}";
        }
    }

    public Brush Brush => new SolidColorBrush(TabColors.Hex(Blocked ? 0xFF9F0Au : Finished ? 0x30D158u : 0x0A84FFu));

    /// <summary>A filled dot for foreground agents, a ring for background ones.</summary>
    public Brush DotFill => Background ? Brushes.Transparent : Brush;

    /// <summary>The tool it's using, for tight spaces: "Grep".</summary>
    public string ShortActivity => Blocked ? "needs you" : Finished ? "done" : Activity?.Split(' ')[0] ?? "starting";

    /// <summary>"Grep "useTheme"" in full, for the agents list.</summary>
    public string ActivityLine => Blocked ? "Needs permission" : Finished ? "Done" : Activity ?? "Starting…";

    public string ToolsText => $"{ToolCount} tool{(ToolCount == 1 ? "" : "s")}" + (Background ? " · background" : "");

    public string Tooltip => $"{Type} · {Activity ?? "starting"}";

    /// <summary>Nested agents are indented under their parent.</summary>
    public Thickness Indent => new((Math.Min(Depth, 3) - 1) * 10, 0, 0, 0);
}

/// <summary>The agent whose transcript is open, kept after the agent drops off the list.</summary>
public sealed record AgentRef(Guid TabId, string AgentId, string Type, string Description, string Transcript)
{
    public AgentRef(Guid tabId, SubAgent agent) : this(tabId, agent.Id, agent.Type, agent.Description, agent.Transcript) { }
}

/// <summary>Watches the subagent folders of running sessions and puts what it finds on each tab's runtime.</summary>
public sealed class AgentMonitor
{
    public static AgentMonitor Shared { get; } = new();

    private readonly AgentScanner _scanner = new();
    private readonly DispatcherTimer _timer;
    private bool _scanning;

    private AgentMonitor()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();

    private async void Poll()
    {
        if (_scanning) return;
        var sessions = new List<AgentScanner.Session>();
        var tabs = new Dictionary<Guid, TabItem>();
        foreach (var (tab, _) in Store.Shared.AllTabs)
        {
            var rt = tab.Runtime;
            var active = !rt.Exited && (rt.Busy || rt.StatusState is StatusState.Working or StatusState.Blocked);
            if (!active && !rt.Agents.Any()) continue;
            sessions.Add(new(tab.Id, tab.ClaudeSessionId.ToString(), active));
            tabs[tab.Id] = tab;
        }
        if (!sessions.Any()) return;
        _scanning = true;
        var found = await Task.Run(() => _scanner.Scan(sessions));
        _scanning = false;
        foreach (var (id, tab) in tabs)
        {
            var blocked = tab.Runtime.StatusChildren.Where(c => c.State == StatusState.Blocked).ToList();
            var agents = found.GetValueOrDefault(id, [])
                .Select(a => a with { Blocked = !a.Finished && blocked.Any(c => Matches(c, a)) })
                .ToList();
            if (!agents.SequenceEqual(tab.Runtime.Agents)) tab.Runtime.Agents = agents;
        }
    }

    /// <summary>Whether an OSC 7501 child record describes this agent: by id, or by the description it was launched with.</summary>
    private static bool Matches(StatusChild child, SubAgent agent)
    {
        var shortId = agent.Id.Replace("agent-", "");
        if (child.Id.Contains(shortId)) return true;
        return new[] { child.Title, child.Message }.Any(t => t?.Contains(agent.Description, StringComparison.OrdinalIgnoreCase) == true);
    }
}

/// <summary>The file work behind AgentMonitor. Used by one scan at a time.</summary>
internal sealed class AgentScanner
{
    public sealed record Session(Guid Tab, string Id, bool Active);

    private sealed class Progress
    {
        public long Offset;
        public byte[] Remainder = [];
        public int Tools;
        public string? Activity;
    }

    /// <summary>How long a finished agent stays listed.</summary>
    private static readonly TimeSpan LingerForeground = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LingerBackground = TimeSpan.FromSeconds(120);

    private readonly Dictionary<string, string> _folders = [];
    private readonly Dictionary<string, DateTime> _misses = [];
    private readonly Dictionary<string, Progress> _progress = [];

    public Dictionary<Guid, List<SubAgent>> Scan(List<Session> sessions)
    {
        var result = new Dictionary<Guid, List<SubAgent>>();
        foreach (var session in sessions)
        {
            if (Folder(session.Id) is not { } folder) continue;
            var agents = Agents(folder, session.Active);
            if (agents.Any()) result[session.Tab] = agents;
        }
        return result;
    }

    /// <summary>The session's subagents folder; it appears with the first subagent.</summary>
    private string? Folder(string session)
    {
        if (_folders.TryGetValue(session, out var known)) return known;
        if (_misses.TryGetValue(session, out var miss) && DateTime.UtcNow - miss < TimeSpan.FromSeconds(3)) return null;
        try
        {
            foreach (var project in Directory.EnumerateDirectories(RecentSession.ProjectsDirectory))
            {
                var folder = Path.Combine(project, session, "subagents");
                if (!Directory.Exists(folder)) continue;
                _folders[session] = folder;
                return folder;
            }
        }
        catch { }
        _misses[session] = DateTime.UtcNow;
        return null;
    }

    private List<SubAgent> Agents(string folder, bool active)
    {
        var now = DateTime.Now;
        var agents = new List<SubAgent>();
        IEnumerable<string> metas;
        try { metas = Directory.EnumerateFiles(folder, "*.meta.json").ToList(); }
        catch { return agents; }
        foreach (var metaPath in metas)
        {
            var id = Path.GetFileName(metaPath)[..^".meta.json".Length];
            var transcript = Path.Combine(folder, id + ".jsonl");
            var metaDate = File.GetLastWriteTime(metaPath);
            var transcriptDate = File.Exists(transcript) ? File.GetLastWriteTime(transcript) : metaDate;
            // Old agents are skipped by date alone, without opening their files: a running agent
            // keeps writing its transcript, a finished one only lingers briefly.
            var recentlyWritten = now - transcriptDate < TimeSpan.FromMinutes(30);
            if (!(now - metaDate < LingerBackground || active && recentlyWritten)) continue;

            JsonElement meta;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                meta = doc.RootElement.Clone();
            }
            catch { continue; }
            var finished = meta.TryGetProperty("finished", out var f) && f.ValueKind == JsonValueKind.True;
            var background = Str(meta, "requestShape") == "background";
            if (finished)
            {
                if (now - metaDate >= (background ? LingerBackground : LingerForeground)) continue;
            }
            else if (!active || !recentlyWritten) continue;

            if (!_progress.TryGetValue(transcript, out var progress)) _progress[transcript] = progress = new Progress();
            Read(transcript, progress);
            var started = File.Exists(transcript) ? File.GetCreationTime(transcript) : metaDate;
            var depth = meta.TryGetProperty("spawnDepth", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var depthValue) ? depthValue : 1;
            agents.Add(new SubAgent(id, Str(meta, "agentType") ?? "agent", Str(meta, "description") ?? id, background,
                Math.Max(1, depth), finished, started, finished ? metaDate : null, transcript, progress.Activity, progress.Tools));
        }
        return agents.OrderBy(a => a.Started).ToList();
    }

    /// <summary>Reads only what was appended since last time, counting tool calls.</summary>
    private static void Read(string path, Progress progress)
    {
        foreach (var line in Transcript.NewLines(path, ref progress.Offset, ref progress.Remainder))
        {
            using var doc = Transcript.Parse(line);
            if (doc is null) continue;
            var record = doc.RootElement;
            if (Str(record, "type") != "assistant" || !Transcript.Content(record, out var content)) continue;
            foreach (var item in content.EnumerateArray())
            {
                if (Str(item, "type") != "tool_use") continue;
                progress.Tools++;
                progress.Activity = Transcript.Summary(Str(item, "name") ?? "Tool", item.TryGetProperty("input", out var input) ? input : default);
            }
        }
    }

    internal static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Reading Claude Code's JSONL transcripts.</summary>
public static class Transcript
{
    /// <summary>The complete lines appended after offset; a partial last line waits in remainder for the next read.</summary>
    public static List<string> NewLines(string path, ref long offset, ref byte[] remainder)
    {
        var lines = new List<string>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= offset) return lines;
            stream.Seek(offset, SeekOrigin.Begin);
            var chunk = new byte[Math.Min(stream.Length - offset, 64L << 20)];
            var read = stream.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false);
            offset += read;
            var data = remainder.Concat(chunk.Take(read)).ToArray();
            var end = Array.LastIndexOf(data, (byte)'\n');
            if (end < 0)
            {
                remainder = data;
                return lines;
            }
            remainder = data[(end + 1)..];
            lines.AddRange(Encoding.UTF8.GetString(data, 0, end).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        catch { }
        return lines;
    }

    public static JsonDocument? Parse(string line)
    {
        try { return JsonDocument.Parse(line); }
        catch { return null; }
    }

    /// <summary>message.content when it is a list of blocks.</summary>
    public static bool Content(JsonElement record, out JsonElement content)
    {
        content = default;
        return record.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("content", out content) && content.ValueKind == JsonValueKind.Array;
    }

    /// <summary>"Grep "useTheme"", "Read app.tsx", "Bash npm test".</summary>
    public static string Summary(string name, JsonElement input)
    {
        string? Field(string key) => AgentScanner.Str(input, key);
        var detail = Field("command");
        if (detail is null && (Field("file_path") ?? Field("notebook_path")) is { } path) detail = Path.GetFileName(path.Replace('/', '\\'));
        if (detail is null && Field("pattern") is { } pattern) detail = $"\"{pattern}\"";
        detail ??= new[] { "url", "query", "description", "prompt", "path" }.Select(Field).FirstOrDefault(v => v is not null) ?? "";
        detail = detail.Replace('\n', ' ');
        if (detail.Length > 90) detail = detail[..90] + "…";
        return detail.Length == 0 ? name : $"{name} {detail}";
    }

    /// <summary>A tool result's text, whether it came as a string or as text blocks.</summary>
    public static string Text(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString() ?? "",
        JsonValueKind.Array => string.Join('\n', content.EnumerateArray()
            .Where(i => AgentScanner.Str(i, "type") == "text").Select(i => AgentScanner.Str(i, "text"))),
        _ => "",
    };
}

public enum TranscriptKind { Task, Text, Tool }

public sealed class TranscriptEntry : Observable
{
    private string? _result;
    private bool _isError;

    public required TranscriptKind Kind { get; init; }
    public required string Title { get; init; }
    public string? Result { get => _result; set { if (Set(ref _result, value)) Raise(nameof(ResultLine)); } }
    public bool IsError { get => _isError; set { if (Set(ref _isError, value)) Raise(nameof(Brush)); } }

    public string ResultLine => "⎿  " + (Result ?? "Running…");
    public Brush Brush => new SolidColorBrush(TabColors.Hex(IsError ? 0xFF453Au : Result is null ? 0x0A84FFu : 0x30D158u));

    /// <summary>Long prompts and replies are cut so the list stays fast.</summary>
    public string Shown => Kind switch
    {
        TranscriptKind.Task when Title.Length > 1500 => Title[..1500] + "…",
        TranscriptKind.Text when Title.Length > 3000 => Title[..3000] + "…",
        _ => Title,
    };
}

/// <summary>A subagent's transcript, read incrementally while it's open.</summary>
public sealed class AgentTranscript : IDisposable
{
    public ObservableCollection<TranscriptEntry> Entries { get; } = [];
    private readonly string _path;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, TranscriptEntry> _tools = [];
    private long _offset;
    private byte[] _remainder = [];
    private bool _loading;

    public AgentTranscript(string path)
    {
        _path = path;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => Load();
        _timer.Start();
        Load();
    }

    public void Dispose() => _timer.Stop();

    private abstract record Event;
    private sealed record TaskEvent(string Text) : Event;
    private sealed record TextEvent(string Text) : Event;
    private sealed record ToolEvent(string Id, string Title) : Event;
    private sealed record ResultEvent(string Id, string Text, bool Error) : Event;

    private async void Load()
    {
        if (_loading) return;
        _loading = true;
        var (offset, remainder, sawTask) = (_offset, _remainder, Entries.Any());
        var events = await Task.Run(() =>
        {
            var list = new List<Event>();
            foreach (var line in Transcript.NewLines(_path, ref offset, ref remainder))
            {
                using var doc = Transcript.Parse(line);
                if (doc is null) continue;
                var record = doc.RootElement;
                switch (AgentScanner.Str(record, "type"))
                {
                    case "user":
                        if (Transcript.Content(record, out var blocks))
                        {
                            foreach (var item in blocks.EnumerateArray().Where(i => AgentScanner.Str(i, "type") == "tool_result"))
                            {
                                var text = item.TryGetProperty("content", out var content) ? Transcript.Text(content).Trim() : "";
                                var error = item.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                                list.Add(new ResultEvent(AgentScanner.Str(item, "tool_use_id") ?? "", text, error));
                            }
                        }
                        else if (record.TryGetProperty("message", out var message) && AgentScanner.Str(message, "content") is { Length: > 0 } prompt)
                        {
                            list.Add(sawTask ? new TextEvent(prompt) : new TaskEvent(prompt));
                            sawTask = true;
                        }
                        break;
                    case "assistant":
                        if (!Transcript.Content(record, out var items)) break;
                        foreach (var item in items.EnumerateArray())
                        {
                            switch (AgentScanner.Str(item, "type"))
                            {
                                case "text" when (AgentScanner.Str(item, "text") ?? "").Trim() is { Length: > 0 } text:
                                    list.Add(new TextEvent(text));
                                    break;
                                case "tool_use":
                                    var title = Transcript.Summary(AgentScanner.Str(item, "name") ?? "Tool", item.TryGetProperty("input", out var input) ? input : default);
                                    list.Add(new ToolEvent(AgentScanner.Str(item, "id") ?? Guid.NewGuid().ToString(), title));
                                    break;
                            }
                        }
                        break;
                }
            }
            return list;
        });
        (_offset, _remainder) = (offset, remainder);
        Apply(events);
        _loading = false;
    }

    private void Apply(List<Event> events)
    {
        foreach (var e in events)
        {
            switch (e)
            {
                case TaskEvent task:
                    Entries.Add(new TranscriptEntry { Kind = TranscriptKind.Task, Title = task.Text });
                    break;
                case TextEvent text:
                    Entries.Add(new TranscriptEntry { Kind = TranscriptKind.Text, Title = text.Text });
                    break;
                case ToolEvent tool:
                    var entry = new TranscriptEntry { Kind = TranscriptKind.Tool, Title = tool.Title };
                    _tools[tool.Id] = entry;
                    Entries.Add(entry);
                    break;
                case ResultEvent result when _tools.TryGetValue(result.Id, out var row):
                    var firstLine = result.Text.Split('\n').FirstOrDefault(l => l.Length > 0) ?? "Done";
                    row.Result = firstLine.Length > 140 ? firstLine[..140] + "…" : firstLine;
                    row.IsError = result.Error;
                    break;
            }
        }
    }
}
