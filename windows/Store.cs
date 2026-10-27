using System.Collections.ObjectModel;
using System.IO;
using System.Media;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace Omux;

public sealed partial class Store : Observable
{
    public static Store Shared { get; } = new();

    public List<TabGroup> Groups { get; private set; } = [];
    /// <summary>Projects followed by their sessions (hidden while collapsed), as the sidebar shows them.</summary>
    public ObservableCollection<object> Rows { get; } = [];
    /// <summary>File paths mentioned in the selected session's output, newest last.</summary>
    public ObservableCollection<string> MentionedPaths { get; } = [];

    /// <summary>The number of sessions waiting on the user changed.</summary>
    public event Action<int>? AttentionChanged;
    /// <summary>Rows were rebuilt; the sidebar restores its selection.</summary>
    public event Action? RowsRebuilt;

    private readonly Dictionary<Guid, PtySession> _terminals = [];
    private readonly Dictionary<Guid, (int Cols, int Rows)> _sizes = [];
    private readonly Dictionary<Guid, DateTime> _busyChecked = [];
    private readonly DispatcherTimer _saveTimer;
    private readonly TerminalSurface _surface = TerminalSurface.Shared;
    private Guid? _selectedTabId;
    private string? _previewRequest;

    private Store()
    {
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        _surface.Input += (id, data) => { if (_terminals.TryGetValue(id, out var pty)) pty.Write(data); };
        _surface.Resized += (id, cols, rows) =>
        {
            _sizes[id] = (cols, rows);
            if (_terminals.TryGetValue(id, out var pty)) pty.Resize(cols, rows);
        };
        _surface.TermEvent += Handle;
    }

    public Guid? SelectedTabId
    {
        get => _selectedTabId;
        set
        {
            if (_selectedTabId == value) return;
            _selectedTabId = value;
            Raise(nameof(SelectedTabId));
            Raise(nameof(SelectedTab));
            Raise(nameof(SelectedGroup));
            ScheduleSave();
            if (value is not { } id) return;
            ShowTerminal(id);
            // Deferred: this fires inside the list's selection update.
            Dispatcher.BeginInvoke(() =>
            {
                ClearAttention(id);
                RefreshBusy(id);
            });
        }
    }

    /// <summary>A file the user Ctrl-clicked in a terminal; the files panel shows it.</summary>
    public string? PreviewRequest { get => _previewRequest; set => Set(ref _previewRequest, value); }

    private static Dispatcher Dispatcher => Application.Current.Dispatcher;

    /// <summary>Restores the saved workspace. Runs once the window exists.</summary>
    public void Load()
    {
        var saved = Persistence.LoadWorkspace();
        Groups = saved?.Groups ?? [];
        if (!Groups.Any()) Groups = [new TabGroup("Default", tabs: [new TabItem("Session 1")])];
        RebuildRows();

        var autoResume = Prefs.Current.AutoResume;
        // Restored tabs stay dormant until selected (shell) or played (claude).
        if (!autoResume) foreach (var (tab, _) in AllTabs) tab.Runtime.Exited = true;

        var allIds = AllTabs.Select(e => e.Tab.Id).ToHashSet();
        if (saved?.SelectedTabId is { } selected && allIds.Contains(selected))
            SelectedTabId = selected;
        else
            SelectedTabId = Groups.FirstOrDefault(g => g.Tabs.Any())?.Tabs[0].Id;

        if (saved is null) Save();
        if (autoResume) StartAllSessions();
    }

    // MARK: Lookup

    public (int Group, int Tab)? Location(Guid tabId)
    {
        for (var g = 0; g < Groups.Count; g++)
        {
            var t = Groups[g].Tabs.FindIndex(tab => tab.Id == tabId);
            if (t >= 0) return (g, t);
        }
        return null;
    }

    public TabItem? Tab(Guid id) => Location(id) is { } loc ? Groups[loc.Group].Tabs[loc.Tab] : null;

    public TabGroup? Group(Guid id) => Groups.FirstOrDefault(g => g.Id == id);

    public TabGroup? GroupContaining(Guid tabId) => Location(tabId) is { } loc ? Groups[loc.Group] : null;

    public TabItem? SelectedTab => SelectedTabId is { } id ? Tab(id) : null;

    public TabGroup? SelectedGroup => (SelectedTabId is { } id ? GroupContaining(id) : null) ?? Groups.FirstOrDefault();

    public IEnumerable<(TabItem Tab, TabGroup Group)> AllTabs => Groups.SelectMany(g => g.Tabs.Select(t => (t, g)));

    public int RunningSessionCount => _terminals.Keys.Count(id => Tab(id) is { } tab && !tab.Runtime.Exited);

    public bool IsLive(Guid id) => _terminals.ContainsKey(id) && Tab(id) is { Runtime.Exited: false };

    // MARK: Terminal lifecycle

    /// <summary>Shows a tab's terminal; a dormant tab gets a plain shell (no claude) on first show.</summary>
    private void ShowTerminal(Guid id)
    {
        if (!_terminals.ContainsKey(id) && Location(id) is { } loc)
            MakeTerminal(Groups[loc.Group].Tabs[loc.Tab], Groups[loc.Group], command: "");
        _surface.Show(id);
    }

    private void StartAllSessions()
    {
        var all = AllTabs.ToList();
        var ordered = all.Where(e => e.Tab.Id == SelectedTabId).Concat(all.Where(e => e.Tab.Id != SelectedTabId)).ToList();
        var delay = TimeSpan.Zero;
        foreach (var (tab, _) in ordered)
        {
            var id = tab.Id;
            After(delay, () =>
            {
                if (_terminals.ContainsKey(id) || Location(id) is not { } loc) return;
                MakeTerminal(Groups[loc.Group].Tabs[loc.Tab], Groups[loc.Group]);
                if (SelectedTabId == id) _surface.Show(id);
            });
            delay += TimeSpan.FromMilliseconds(250);
        }
    }

    private void MakeTerminal(TabItem tab, TabGroup group, string? command = null)
    {
        _surface.Create(tab.Id);
        Launch(tab, group, command);
    }

    private void Launch(TabItem tab, TabGroup group, string? command)
    {
        var dir = Paths.Expand(tab.Directory ?? group.Directory ?? Paths.Home);
        if (!Directory.Exists(dir)) dir = Paths.Home;

        var startup = command ?? StartupCommand(tab, group);
        var shell = ShellCommand.Resolve();
        var (cols, rows) = _sizes.TryGetValue(tab.Id, out var size) ? size : (120, 30);

        PtySession pty;
        try
        {
            pty = PtySession.Start(shell.CommandLine(startup), dir, ShellCommand.Environment(), (short)cols, (short)rows);
        }
        catch (Exception error)
        {
            _surface.Write(tab.Id, $"\r\n\x1b[31momux could not start {shell.Executable}: {error.Message}\x1b[0m\r\n");
            Update(tab, rt => rt.Exited = true);
            return;
        }

        var id = tab.Id;
        _terminals[id] = pty;
        pty.Output += data => _surface.Write(id, data);
        pty.Exited += () => Dispatcher.BeginInvoke(() =>
        {
            if (!_terminals.TryGetValue(id, out var live) || live != pty) return;
            _surface.ProcessExited(id);
            if (Tab(id) is { } exitedTab) Update(exitedTab, rt => { rt.Exited = true; rt.StatusState = null; rt.Busy = false; });
        });
        Update(tab, rt => { rt.Exited = false; rt.Cwd = dir; rt.StatusState = null; rt.Title = ""; });

        // Shells without a "run this, then stay" flag get the command typed in, as on macOS.
        if (!string.IsNullOrEmpty(startup) && !shell.RunsStartupCommand)
            After(TimeSpan.FromMilliseconds(500), () => pty.Write(startup + "\r"));
    }

    /// <summary>Kills the shell and everything it spawned (claude, node, …), not just the shell.</summary>
    private void KillTree(Guid id)
    {
        if (!_terminals.Remove(id, out var pty)) return;
        pty.Dispose();
    }

    /// <summary>
    /// Interrupts whatever runs in the shell, like Ctrl-C. Sent twice because Claude Code
    /// asks for a second Ctrl-C to exit. The shell itself stays at its prompt.
    /// </summary>
    public void Stop(Guid id)
    {
        if (!_terminals.TryGetValue(id, out var pty) || !IsLive(id)) return;
        pty.Write("\x03");
        After(TimeSpan.FromMilliseconds(350), () => pty.Write("\x03"));
        After(TimeSpan.FromMilliseconds(1500), () => RefreshBusy(id, force: true));
    }

    /// <summary>Force-kills the session's whole process tree; the tab shows as exited.</summary>
    public void Kill(Guid id)
    {
        if (!_terminals.ContainsKey(id)) return;
        KillTree(id);
        _surface.ProcessExited(id);
        if (Tab(id) is { } tab) Update(tab, rt => { rt.Exited = true; rt.StatusState = null; rt.Busy = false; });
    }

    /// <summary>Rescans the selected session's scrollback for file paths.</summary>
    public async void ScanMentionedPaths()
    {
        if (SelectedTabId is not { } id || !_terminals.ContainsKey(id))
        {
            MentionedPaths.Clear();
            return;
        }
        var text = await _surface.BufferTextAsync(id);
        var paths = await Task.Run(() => FindPaths(text));
        if (SelectedTabId != id || paths.SequenceEqual(MentionedPaths)) return;
        MentionedPaths.Clear();
        foreach (var path in paths) MentionedPaths.Add(path);
    }

    [GeneratedRegex(@"(?<![\w])(?:[A-Za-z]:[\\/]|~[\\/])[\w.+@%~\\/-]+")]
    private static partial Regex PathPattern();

    /// <summary>File paths that appear in the text and still exist, oldest first.</summary>
    private static List<string> FindPaths(string text)
    {
        var result = new List<string>();
        foreach (Match match in PathPattern().Matches(text))
        {
            if (FilePath(match.Value) is not { } path) continue;
            result.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            result.Add(path);
        }
        return result;
    }

    /// <summary>An existing file for a path or file:// link from terminal output, else null.</summary>
    public static string? FilePath(string link)
    {
        var raw = link.Trim();
        if (raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(raw, UriKind.Absolute, out var uri)) raw = uri.LocalPath;
        if (!(raw.Length > 2 && char.IsAsciiLetter(raw[0]) && raw[1] == ':') && !raw.StartsWith('~')) return null;
        // Drop trailing punctuation Claude's prose leaves on paths.
        raw = raw.TrimEnd('.', ',', ':', ';', ')', ']', '}', '\'', '"');
        try
        {
            var path = Path.GetFullPath(Paths.Expand(raw));
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    /// <summary>Re-checks whether the shell has a child process (claude, a build, …).</summary>
    public void RefreshBusy(Guid id, bool force = false)
    {
        if (!_terminals.TryGetValue(id, out var pty) || Tab(id) is not { } tab || tab.Runtime.Exited) return;
        // Claude Code animates its title; a process snapshot per frame is wasted work.
        var now = DateTime.UtcNow;
        if (!force && _busyChecked.TryGetValue(id, out var last) && now - last < TimeSpan.FromMilliseconds(500)) return;
        _busyChecked[id] = now;
        var busy = pty.HasChildProcess();
        Update(tab, rt => rt.Busy = busy);
    }

    /// <summary>
    /// Continues the tab's Claude conversation: relaunches the tab if it was stopped,
    /// otherwise runs the command in its live shell.
    /// </summary>
    public void Start(Guid id)
    {
        if (Location(id) is not { } loc) return;
        var tab = Groups[loc.Group].Tabs[loc.Tab];
        var session = tab.ClaudeSessionId.ToString();
        var command = RecentSession.Exists(session) ? $"claude --resume {session}" : "claude --continue";
        if (_terminals.TryGetValue(id, out var pty) && !tab.Runtime.Exited)
        {
            RefreshBusy(id, force: true);
            if (tab.Runtime.Busy) return;
            pty.Write(command + "\r");
        }
        else
        {
            KillTree(id);
            _surface.Destroy(id);
            MakeTerminal(tab, Groups[loc.Group], command);
            if (SelectedTabId == id) _surface.Show(id);
        }
        After(TimeSpan.FromMilliseconds(1500), () => RefreshBusy(id, force: true));
    }

    /// <summary>
    /// Resolves the tab's command and, for claude, pins it to the tab's session id so the
    /// conversation resumes on relaunch.
    /// </summary>
    public static string? StartupCommand(TabItem tab, TabGroup group)
    {
        var raw = (tab.Command ?? group.Command ?? "").Trim();
        if (raw.Length == 0) return null;
        var parts = raw.Split(' ', 2);
        if (parts[0] != "claude") return raw;
        var rest = parts.Length > 1 ? parts[1] : "";
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Intersect(["--resume", "-r", "--continue", "-c", "--session-id"]).Any()) return raw;
        var session = tab.ClaudeSessionId.ToString();
        var flag = RecentSession.Exists(session) ? $"--resume {session}" : $"--session-id {session}";
        return string.Join(' ', new[] { "claude", flag, rest }.Where(s => s.Length > 0));
    }

    /// <summary>Opens a folder as a new group with one Claude session inside it.</summary>
    public void OpenFolder(string path)
    {
        path = path.Trim().Trim('"');
        if (!Directory.Exists(path)) return;
        path = Path.GetFullPath(path).TrimEnd('\\');
        if (path.EndsWith(':')) path += "\\";
        if (Groups.FirstOrDefault(g => Paths.Same(g.Directory, path)) is { } existing)
        {
            NewTab(existing.Id);
            return;
        }
        var group = new TabGroup(Paths.LastComponent(path), path, [new TabItem("Session 1")]);
        Groups.Add(group);
        Touch(structural: true);
        MakeTerminal(group.Tabs[0], group);
        SelectedTabId = group.Tabs[0].Id;
    }

    /// <summary>Opens an existing Claude conversation as a session in the project for its folder.</summary>
    public void OpenClaudeSession(RecentSession recent)
    {
        if (AllTabs.FirstOrDefault(e => e.Tab.ClaudeSessionId == recent.Id) is { Tab: not null } existing)
        {
            SelectedTabId = existing.Tab.Id;
            return;
        }
        var group = Groups.FirstOrDefault(g => Paths.Same(g.Directory, recent.Cwd));
        if (group is null)
        {
            group = new TabGroup(Paths.LastComponent(recent.Cwd), recent.Cwd);
            Groups.Add(group);
        }
        var tab = new TabItem(recent.TabName.Length == 0 ? "Session" : recent.TabName) { ClaudeSessionId = recent.Id };
        group.Tabs.Add(tab);
        group.IsCollapsed = false;
        Touch(structural: true);
        MakeTerminal(tab, group);
        SelectedTabId = tab.Id;
    }

    public void OpenFolderPanel()
    {
        foreach (var folder in Desktop.ChooseFolders() ?? []) OpenFolder(folder);
    }

    private void Handle(Guid id, TermEvent e)
    {
        if (Tab(id) is not { } tab) return;
        switch (e.Kind)
        {
            case TermEventKind.Bell:
                SystemSounds.Beep.Play();
                if (!IsWatching(id)) Update(tab, rt => rt.NeedsAttention = true);
                break;
            case TermEventKind.Title:
                RefreshBusy(id);
                var title = ShellCommand.IsShellTitle(e.Text) ? "" : e.Text ?? "";
                // Claude Code rewrites the title as its state changes; treat that as activity.
                Update(tab, rt =>
                {
                    if (rt.Title != title && !IsWatching(id)) rt.HasActivity = true;
                    rt.Title = title;
                });
                break;
            case TermEventKind.Directory:
                Update(tab, rt => rt.Cwd = e.Text);
                break;
            case TermEventKind.OpenPath:
                if (FilePath(e.Text ?? "") is { } path) PreviewRequest = path;
                break;
            case TermEventKind.Status:
                Update(tab, rt =>
                {
                    rt.StatusState = e.State;
                    rt.StatusDetail = e.Text;
                    if (e.State is StatusState.Blocked or StatusState.Done && !IsWatching(id)) rt.NeedsAttention = true;
                });
                break;
        }
    }

    private bool IsWatching(Guid id) => SelectedTabId == id && Application.Current.MainWindow?.IsActive == true;

    public void ClearAttention(Guid id)
    {
        if (Tab(id) is { } tab) Update(tab, rt => { rt.HasActivity = false; rt.NeedsAttention = false; });
    }

    private void Update(TabItem tab, Action<TabRuntime> mutate)
    {
        var before = tab.Runtime.NeedsAttention;
        mutate(tab.Runtime);
        if (before != tab.Runtime.NeedsAttention) AttentionChanged?.Invoke(AllTabs.Count(e => e.Tab.Runtime.NeedsAttention));
    }

    // MARK: Tabs

    public void NewTab(Guid? groupId = null, string? command = null)
    {
        var group = Groups.FirstOrDefault(g => g.Id == (groupId ?? SelectedGroup?.Id)) ?? Groups.FirstOrDefault();
        if (group is null)
        {
            NewGroup();
            return;
        }
        var tab = new TabItem($"Session {group.Tabs.Count + 1}", command: command);
        group.Tabs.Add(tab);
        group.IsCollapsed = false;
        Touch(structural: true);
        MakeTerminal(tab, group);
        SelectedTabId = tab.Id;
    }

    public void CloseTab(Guid id)
    {
        if (Location(id) is not { } loc) return;
        KillTree(id);
        _surface.Destroy(id);
        _sizes.Remove(id);
        var group = Groups[loc.Group];
        if (group.Tabs[loc.Tab].Runtime.NeedsAttention) Update(group.Tabs[loc.Tab], rt => rt.NeedsAttention = false);
        group.Tabs.RemoveAt(loc.Tab);
        if (SelectedTabId == id)
        {
            var siblings = group.Tabs;
            SelectedTabId = siblings.Any() ? siblings[Math.Min(loc.Tab, siblings.Count - 1)].Id : AllTabs.FirstOrDefault().Tab?.Id;
        }
        Touch(structural: true);
    }

    public void Restart(Guid id)
    {
        if (Location(id) is not { } loc) return;
        KillTree(id);
        _surface.Destroy(id);
        MakeTerminal(Groups[loc.Group].Tabs[loc.Tab], Groups[loc.Group]);
        if (SelectedTabId == id) _surface.Show(id);
    }

    public void BeginRename(Guid? id)
    {
        foreach (var group in Groups)
        {
            group.IsRenaming = group.Id == id;
            foreach (var tab in group.Tabs) tab.IsRenaming = tab.Id == id;
        }
    }

    public void Rename(Guid id, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return;
        if (Tab(id) is { } tab) tab.Name = trimmed;
        else if (Group(id) is { } group) group.Name = trimmed;
        Touch();
    }

    public void MoveTab(Guid id, Guid groupId)
    {
        if (Location(id) is not { } loc || Group(groupId) is not { } target || Groups[loc.Group] == target) return;
        var tab = Groups[loc.Group].Tabs[loc.Tab];
        Groups[loc.Group].Tabs.RemoveAt(loc.Tab);
        target.Tabs.Add(tab);
        target.IsCollapsed = false;
        Touch(structural: true);
    }

    // MARK: Groups

    public void NewGroup()
    {
        var group = new TabGroup($"Project {Groups.Count + 1}", tabs: [new TabItem("Session 1")]);
        Groups.Add(group);
        Touch(structural: true);
        MakeTerminal(group.Tabs[0], group);
        SelectedTabId = group.Tabs[0].Id;
        BeginRename(group.Id);
    }

    public void DeleteGroup(Guid id)
    {
        if (Group(id) is not { } group) return;
        var running = group.Tabs.Count(t => !t.Runtime.Exited && _terminals.ContainsKey(t.Id));
        if (running > 0)
        {
            var answer = MessageBox.Show(Application.Current.MainWindow!,
                $"{running} running session{(running == 1 ? "" : "s")} will be closed.",
                $"Delete “{group.Name}”?", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;
        }
        foreach (var tab in group.Tabs.ToList()) CloseTab(tab.Id);
        Groups.Remove(group);
        Touch(structural: true);
        if (!Groups.Any()) NewGroup();
        else if (SelectedTab is null) SelectedTabId = AllTabs.FirstOrDefault().Tab?.Id;
    }

    public void ToggleCollapsed(Guid groupId)
    {
        if (Group(groupId) is not { } group) return;
        group.IsCollapsed = !group.IsCollapsed;
        Touch(structural: true);
    }

    public void MoveGroup(Guid groupId, int offset)
    {
        var from = Groups.FindIndex(g => g.Id == groupId);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= Groups.Count) return;
        (Groups[from], Groups[to]) = (Groups[to], Groups[from]);
        Touch(structural: true);
    }

    /// <summary>
    /// Applies a drag in the flat sidebar list. Moving a project row reorders projects;
    /// moving a session row places it under whichever project row ends up above it.
    /// </summary>
    public void MoveRow(object row, int destination)
    {
        var rows = Rows.ToList();
        var from = rows.IndexOf(row);
        if (from < 0) return;
        rows.RemoveAt(from);
        rows.Insert(Math.Clamp(destination > from ? destination - 1 : destination, 0, rows.Count), row);
        switch (row)
        {
            case TabGroup:
                var order = rows.OfType<TabGroup>().ToList();
                Groups = Groups.OrderBy(g => order.IndexOf(g)).ToList();
                break;
            case TabItem tab:
                var newIndex = rows.IndexOf(tab);
                var target = rows.Take(newIndex).OfType<TabGroup>().LastOrDefault() ?? Groups.FirstOrDefault();
                if (target is null || Location(tab.Id) is not { } loc) return;
                var position = newIndex - rows.IndexOf(target) - 1;
                Groups[loc.Group].Tabs.RemoveAt(loc.Tab);
                target.Tabs.Insert(Math.Clamp(position, 0, target.Tabs.Count), tab);
                target.IsCollapsed = false;
                break;
        }
        Touch(structural: true);
    }

    // MARK: Navigation

    public void SelectIndex(int index)
    {
        if (SelectedGroup is { } group && index >= 0 && index < group.Tabs.Count) SelectedTabId = group.Tabs[index].Id;
    }

    public void SelectRelative(int offset)
    {
        var tabs = AllTabs.Select(e => e.Tab.Id).ToList();
        if (!tabs.Any()) return;
        var current = SelectedTabId is { } id ? Math.Max(tabs.IndexOf(id), 0) : 0;
        SelectedTabId = tabs[((current + offset) % tabs.Count + tabs.Count) % tabs.Count];
    }

    // MARK: Persistence

    /// <summary>Records a change to the layout: saved shortly after, and the sidebar rebuilt if rows moved.</summary>
    public void Touch(bool structural = false)
    {
        if (structural) RebuildRows();
        ScheduleSave();
    }

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var group in Groups)
        {
            Rows.Add(group);
            if (!group.IsCollapsed) foreach (var tab in group.Tabs) Rows.Add(tab);
        }
        RowsRebuilt?.Invoke();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save() => Persistence.Save(new Workspace { Groups = Groups, SelectedTabId = SelectedTabId });

    public void ApplyAppearance()
    {
        _surface.ApplyAppearance(Theme.Current);
        foreach (var (tab, _) in AllTabs) tab.Runtime.Lines();
    }

    /// <summary>Saves the layout and kills every session's process tree, running or not.</summary>
    public void PrepareForTermination()
    {
        _saveTimer.Stop();
        Save();
        foreach (var pty in _terminals.Values) pty.Kill();
    }

    private static void After(TimeSpan delay, Action action)
    {
        if (delay <= TimeSpan.Zero)
        {
            Dispatcher.BeginInvoke(action);
            return;
        }
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }
}
