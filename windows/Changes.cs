using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Omux;

public sealed record DiffNote(string Path, int LineIndex, int? LineNumber, string Text);

/// <summary>The selected session's changes, kept fresh while its folder changes.</summary>
public sealed class Changes : Observable
{
    public static Changes Shared { get; } = new();

    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<Guid, List<DiffNote>> _notes = [];
    private FileSystemWatcher? _watcher;
    private Worktree? _worktree;
    private string? _base;
    private bool _refreshing;
    private bool _again;
    private Guid? _tabId;
    private IReadOnlyList<ChangedFile> _files = [];
    private bool _isRepo;
    private string _baseName = "HEAD";

    /// <summary>Review notes changed, for the selected session.</summary>
    public event Action? NotesChanged;

    private Changes()
    {
        // Folder changes are coalesced to one refresh per second.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); Refresh(); };
    }

    public Guid? TabId { get => _tabId; private set => Set(ref _tabId, value); }
    public string? Dir { get; private set; }
    public IReadOnlyList<ChangedFile> Files { get => _files; private set { if (Set(ref _files, value)) Raise(nameof(Totals)); } }
    public bool IsRepo { get => _isRepo; private set => Set(ref _isRepo, value); }
    /// <summary>What the files are compared with, for display: the base branch or HEAD.</summary>
    public string BaseName { get => _baseName; private set => Set(ref _baseName, value); }

    public (int Added, int Removed) Totals => (Files.Sum(f => f.Added ?? 0), Files.Sum(f => f.Removed ?? 0));

    public IReadOnlyList<DiffNote> Notes => TabId is { } id && _notes.TryGetValue(id, out var list) ? list : [];

    /// <summary>Follows the selected session. Folders outside a repo aren't watched, so a session in the home folder costs nothing.</summary>
    public async void Track(Guid? tabId, string? dir, Worktree? worktree)
    {
        if (tabId == TabId && dir == Dir && worktree == _worktree) return;
        TabId = tabId;
        Dir = dir;
        _worktree = worktree;
        Files = [];
        _base = null;
        _watcher?.Dispose();
        _watcher = null;
        IsRepo = false;
        BaseName = worktree?.Base ?? "HEAD";
        NotesChanged?.Invoke();
        if (dir is null || !Directory.Exists(dir)) return;
        var inRepo = await Task.Run(() => Git.TopLevel(dir) is not null);
        if (Dir != dir || !inRepo) return;
        IsRepo = true;
        Watch(dir);
        Refresh();
    }

    private void Watch(string dir)
    {
        try
        {
            _watcher = new FileSystemWatcher(dir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
            FileSystemEventHandler changed = (_, _) => Application.Current.Dispatcher.BeginInvoke(() => { if (!_refreshTimer.IsEnabled) _refreshTimer.Start(); });
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Changed += changed;
            _watcher.Renamed += (s, e) => changed(s, e);
            _watcher.EnableRaisingEvents = true;
        }
        catch { _watcher = null; }
    }

    public async void Refresh()
    {
        if (Dir is not { } dir || !IsRepo) return;
        if (_refreshing)
        {
            _again = true;
            return;
        }
        _refreshing = true;
        var worktree = _worktree;
        var (@base, files) = await Task.Run(() =>
        {
            var diffBase = Git.DiffBase(dir, worktree);
            return (diffBase, Git.ChangedFiles(dir, diffBase));
        });
        _refreshing = false;
        if (Dir == dir)
        {
            _base = @base;
            if (!files.SequenceEqual(Files)) Files = files;
        }
        if (_again || Dir != dir)
        {
            _again = false;
            Refresh();
        }
    }

    public async Task<List<DiffLine>> Diff(ChangedFile file)
    {
        if (Dir is not { } dir) return [];
        var @base = _base;
        return await Task.Run(() => Git.Diff(dir, @base, file));
    }

    public void SetNote(string text, DiffLine line, string path)
    {
        if (TabId is not { } id) return;
        if (!_notes.TryGetValue(id, out var list)) _notes[id] = list = [];
        list.RemoveAll(n => n.Path == path && n.LineIndex == line.Index);
        var trimmed = text.Trim();
        if (trimmed.Length > 0) list.Add(new DiffNote(path, line.Index, line.Number, trimmed));
        NotesChanged?.Invoke();
    }

    public void RemoveNote(DiffNote note)
    {
        if (TabId is { } id && _notes.TryGetValue(id, out var list) && list.Remove(note)) NotesChanged?.Invoke();
    }

    /// <summary>Types every note into the session as one message and submits it.</summary>
    public void SendNotes()
    {
        if (TabId is not { } id || !_notes.TryGetValue(id, out var list) || !list.Any()) return;
        var body = string.Join('\n', list.Select(n => $"- {n.Path}{(n.LineNumber is { } number ? $":{number}" : "")}: {n.Text}"));
        Store.Shared.SendText(id, "Review notes on your changes:\n" + body, submit: true);
        list.Clear();
        NotesChanged?.Invoke();
    }
}
