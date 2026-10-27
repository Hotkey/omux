using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Omux;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum TabColor { None, Red, Orange, Yellow, Green, Mint, Teal, Cyan, Blue, Indigo, Purple, Pink, Brown, Gray }

public static class TabColors
{
    public static Color? Color(this TabColor tabColor) => tabColor switch
    {
        TabColor.Red => Hex(0xFF3B30),
        TabColor.Orange => Hex(0xFF9500),
        TabColor.Yellow => Hex(0xFFCC00),
        TabColor.Green => Hex(0x34C759),
        TabColor.Mint => Hex(0x00C7BE),
        TabColor.Teal => Hex(0x30B0C7),
        TabColor.Cyan => Hex(0x32ADE6),
        TabColor.Blue => Hex(0x007AFF),
        TabColor.Indigo => Hex(0x5856D6),
        TabColor.Purple => Hex(0xAF52DE),
        TabColor.Pink => Hex(0xFF2D55),
        TabColor.Brown => Hex(0xA2845E),
        TabColor.Gray => Hex(0x8E8E93),
        _ => null,
    };

    public static string Label(this TabColor tabColor) => tabColor.ToString();

    public static Color Hex(uint hex) => System.Windows.Media.Color.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
}

public sealed class TabItem : Observable
{
    private string _name = "Session";
    private string? _directory;
    private string? _command;
    private TabColor _color = TabColor.None;
    private bool _isRenaming;

    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("name")] public string Name { get => _name; set => Set(ref _name, value); }
    /// <summary>null inherits the group's directory (or the home folder).</summary>
    [JsonPropertyName("directory")] public string? Directory { get => _directory; set => Set(ref _directory, value); }
    /// <summary>null inherits the group's command; "" means a plain shell.</summary>
    [JsonPropertyName("command")] public string? Command { get => _command; set => Set(ref _command, value); }
    [JsonPropertyName("color")] public TabColor Color { get => _color; set { if (Set(ref _color, value)) Raise(nameof(DotBrush)); } }
    /// <summary>Stable Claude Code session id so the conversation resumes across relaunches.</summary>
    [JsonPropertyName("claudeSessionID")] public Guid ClaudeSessionId { get; set; } = Guid.NewGuid();

    [JsonIgnore] public TabRuntime Runtime { get; }
    [JsonIgnore] public bool IsRenaming { get => _isRenaming; set => Set(ref _isRenaming, value); }

    /// <summary>Live status colour, else the tab's colour, else a dim dot.</summary>
    [JsonIgnore]
    public Brush DotBrush
    {
        get
        {
            if (Runtime.StatusColor is { } status) return new SolidColorBrush(status);
            if (Color.Color() is { } color) return new SolidColorBrush(color);
            return (Brush)System.Windows.Application.Current.Resources["DimDotBrush"];
        }
    }

    public TabItem()
    {
        Runtime = new TabRuntime();
        Runtime.PropertyChanged += (_, _) => Raise(nameof(DotBrush));
    }

    public TabItem(string name, string? directory = null, string? command = null) : this()
    {
        _name = name;
        _directory = directory;
        _command = command;
    }

    public void RefreshDot() => Raise(nameof(DotBrush));
}

public sealed class TabGroup : Observable
{
    private string _name = "Project";
    private string? _directory;
    private string? _command = "claude";
    private bool _isCollapsed;
    private bool _isRenaming;

    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("name")] public string Name { get => _name; set => Set(ref _name, value); }
    [JsonPropertyName("directory")] public string? Directory { get => _directory; set => Set(ref _directory, value); }
    [JsonPropertyName("command")] public string? Command { get => _command; set => Set(ref _command, value); }
    [JsonPropertyName("isCollapsed")] public bool IsCollapsed { get => _isCollapsed; set => Set(ref _isCollapsed, value); }
    [JsonPropertyName("tabs")] public List<TabItem> Tabs { get; set; } = [];

    [JsonIgnore] public bool IsRenaming { get => _isRenaming; set => Set(ref _isRenaming, value); }

    public TabGroup() { }

    public TabGroup(string name, string? directory = null, List<TabItem>? tabs = null)
    {
        _name = name;
        _directory = directory;
        Tabs = tabs ?? [];
    }
}

public sealed class Workspace
{
    [JsonPropertyName("groups")] public List<TabGroup> Groups { get; set; } = [];
    [JsonPropertyName("selectedTabID")] public Guid? SelectedTabId { get; set; }
}

public enum StatusState { Idle, Working, Done, Blocked, Error }

/// <summary>Live, non-persisted state of one terminal tab.</summary>
public sealed class TabRuntime : Observable
{
    private string _title = "";
    private string? _cwd;
    private bool _hasActivity;
    private bool _needsAttention;
    private bool _exited;
    private bool _busy;
    private StatusState? _statusState;
    private string? _statusDetail;

    public string Title { get => _title; set { if (Set(ref _title, value)) Lines(); } }
    public string? Cwd { get => _cwd; set { if (Set(ref _cwd, value)) Lines(); } }
    public bool HasActivity { get => _hasActivity; set => Set(ref _hasActivity, value); }
    public bool NeedsAttention { get => _needsAttention; set => Set(ref _needsAttention, value); }
    public bool Exited { get => _exited; set { if (Set(ref _exited, value)) Lines(); } }
    /// <summary>A program (e.g. claude) is running in the shell, as opposed to an idle prompt.</summary>
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public StatusState? StatusState { get => _statusState; set { if (Set(ref _statusState, value)) { Lines(); Raise(nameof(StatusColor)); } } }
    public string? StatusDetail { get => _statusDetail; set { if (Set(ref _statusDetail, value)) Lines(); } }

    /// <summary>Status under the session name in the sidebar.</summary>
    public string? SidebarLine => StatusLine(Prefs.Current.ShowFolderPath);
    /// <summary>Status next to the session name in the header.</summary>
    public string? HeaderLine => StatusLine(showFolder: false);

    public string? StatusLine(bool showFolder = true)
    {
        switch (StatusState)
        {
            case Omux.StatusState.Blocked: return StatusDetail is { } b ? $"Needs you · {b}" : "Needs you";
            case Omux.StatusState.Working: return StatusDetail is { } w ? $"Working · {w}" : "Working…";
            case Omux.StatusState.Done: return "Done";
            case Omux.StatusState.Error: return StatusDetail is { } e ? $"Error · {e}" : "Error";
        }
        if (Exited) return "Stopped";
        if (Title.Length > 0) return Title;
        if (showFolder && Cwd is { } cwd) return Paths.Abbreviate(cwd);
        return null;
    }

    public Color? StatusColor => StatusState switch
    {
        Omux.StatusState.Blocked => TabColors.Hex(0xFF9500),
        Omux.StatusState.Working => TabColors.Hex(0x0A84FF),
        Omux.StatusState.Done => TabColors.Hex(0x30D158),
        Omux.StatusState.Error => TabColors.Hex(0xFF453A),
        _ => null,
    };

    public void Lines()
    {
        Raise(nameof(SidebarLine));
        Raise(nameof(HeaderLine));
    }
}

public static class Paths
{
    public static string Home { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Abbreviate(string path) =>
        path.StartsWith(Home, StringComparison.OrdinalIgnoreCase) ? "~" + path[Home.Length..] : path;

    public static string Expand(string path) =>
        path == "~" ? Home
        : path.StartsWith("~\\") || path.StartsWith("~/") ? Path.Combine(Home, path[2..])
        : Environment.ExpandEnvironmentVariables(path);

    public static string LastComponent(string path) =>
        Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path;

    /// <summary>Same folder, ignoring case and trailing separators.</summary>
    public static bool Same(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

public static class Persistence
{
    public static string Directory { get; } = CreateDirectory();

    private static string CreateDirectory()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "omux");
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    public static string WorkspacePath => Path.Combine(Directory, "workspace.json");
    public static string BackupPath => Path.Combine(Directory, "workspace.json.bak");

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Falls back to the previous save if the current file is missing or unreadable.</summary>
    public static Workspace? LoadWorkspace()
    {
        foreach (var path in new[] { WorkspacePath, BackupPath })
        {
            try
            {
                var workspace = JsonSerializer.Deserialize<Workspace>(File.ReadAllText(path), Json);
                if (workspace is not null && workspace.Groups.Any()) return workspace;
            }
            catch { }
        }
        return null;
    }

    public static void Save(Workspace workspace)
    {
        try
        {
            var data = JsonSerializer.Serialize(workspace, Json);
            if (File.Exists(WorkspacePath))
            {
                var current = File.ReadAllText(WorkspacePath);
                if (current != data) File.WriteAllText(BackupPath, current);
            }
            var temp = WorkspacePath + ".tmp";
            File.WriteAllText(temp, data);
            File.Move(temp, WorkspacePath, overwrite: true);
        }
        catch { }
    }
}
