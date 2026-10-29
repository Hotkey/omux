using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Omux;

/// <summary>
/// A menu command and its keyboard shortcut (⌘ on macOS becomes Ctrl+Shift here). AlternateKey
/// takes the same modifiers, for keys some layouts lack (Turkish Q has no = key, so numpad + zooms).
/// </summary>
public sealed record AppCommand(string Name, string Title, Key Key, ModifierKeys Modifiers, Action Run, Key AlternateKey = Key.None)
{
    public string Gesture
    {
        get
        {
            if (Key == Key.None) return "";
            var parts = new List<string>();
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            parts.Add(KeyName(Key));
            var gesture = string.Join('+', parts);
            return AlternateKey == Key.None ? gesture : $"{gesture} ({KeyName(AlternateKey)})";
        }
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture),
        Key.OemPeriod => ".",
        Key.OemComma => ",",
        Key.OemPlus => "=",
        Key.OemMinus => "-",
        Key.Add => "Num +",
        Key.Subtract => "Num -",
        Key.Enter => "Enter",
        _ => key.ToString(),
    };

    /// <summary>
    /// The DOM KeyboardEvent.code, so the terminal page can hand the shortcut back. Codes name
    /// physical keys, so punctuation (which moves between layouts) are left to WPF's own key handling.
    /// </summary>
    public string? Code => CodeOf(Key);

    public string? AlternateCode => CodeOf(AlternateKey);

    public bool Matches(Key key, ModifierKeys modifiers) =>
        Key != Key.None && modifiers == Modifiers && (key == Key || (AlternateKey != Key.None && key == AlternateKey));

    private static string? CodeOf(Key key) => key switch
    {
        >= Key.A and <= Key.Z => "Key" + key,
        >= Key.D0 and <= Key.D9 => "Digit" + (int)(key - Key.D0),
        >= Key.F1 and <= Key.F12 => key.ToString(),
        Key.Add => "NumpadAdd",
        Key.Subtract => "NumpadSubtract",
        Key.Enter => "Enter",
        Key.Tab => "Tab",
        _ => null,
    };
}

public partial class MainWindow : Window
{
    public static MainWindow Instance => (MainWindow)Application.Current.MainWindow!;

    public IReadOnlyList<AppCommand> Commands { get; }
    public CoreWebView2Environment? WebEnvironment { get; private set; }

    private readonly Store _store = Store.Shared;
    private readonly string[] _launchFolders;
    private (string Name, DateTime At) _lastCommand;
    private double _filesWidth = 380;
    private int _attentionCount;

    public MainWindow(string[] launchFolders)
    {
        InitializeComponent();
        _launchFolders = launchFolders;
        Commands = BuildCommands();
        RestorePlacement();
        TerminalHost.Content = TerminalSurface.Shared;

        SourceInitialized += (_, _) => Desktop.StyleTitleBar(this, Theme.Current);
        Loaded += OnLoaded;
        Closing += OnClosing;
        Activated += (_, _) => { if (_store.SelectedTabId is { } id) _store.ClearAttention(id); };
        PreviewKeyDown += OnPreviewKeyDown;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) foreach (var path in paths.Where(Directory.Exists)) _store.OpenFolder(path);
        };
        _store.AttentionChanged += UpdateBadge;
        _store.PropertyChanged += OnStoreChanged;
        TerminalSurface.Shared.Shortcut += Run;
        Prefs.Current.PropertyChanged += OnPrefsChanged;
        ApplyFilesVisibility();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "omux", "WebView2");
            WebEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
            await TerminalSurface.Shared.InitializeAsync(WebEnvironment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "omux needs the Microsoft Edge WebView2 Runtime.\n\nInstall it from https://developer.microsoft.com/microsoft-edge/webview2/ and start omux again.",
                "WebView2 Runtime missing", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
            return;
        }

        TerminalSurface.Shared.ApplyAppearance(Theme.Current);
        TerminalSurface.Shared.SetShortcuts(Commands.SelectMany(c => new[] { c.Code, c.AlternateCode }.OfType<string>().Select(code => new
        {
            name = c.Name,
            code,
            ctrl = c.Modifiers.HasFlag(ModifierKeys.Control),
            shift = c.Modifiers.HasFlag(ModifierKeys.Shift),
            alt = c.Modifiers.HasFlag(ModifierKeys.Alt),
        })));
        _store.Load();
        Right.Files.Initialize(WebEnvironment);
        OpenFromOutside(_launchFolders);
        UpdateNoSession();
        TrackChanges();
        AgentMonitor.Shared.Start();
        PullRequestMonitor.Shared.Start(this);
    }

    /// <summary>The Changes tab and the branch chip follow the selected session and its folder.</summary>
    private void TrackChanges()
    {
        var tab = _store.SelectedTab;
        Changes.Shared.Track(tab?.Id, tab is null ? null : _store.FolderOf(tab), tab?.Worktree);
    }

    private List<AppCommand> BuildCommands()
    {
        const ModifierKeys CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;
        const ModifierKeys CtrlShiftAlt = CtrlShift | ModifierKeys.Alt;
        void OnSelected(Action<Guid> action) { if (_store.SelectedTabId is { } id) action(id); }

        var commands = new List<AppCommand>
        {
            new("newSession", "New Session", Key.T, CtrlShift, () => _store.NewTab()),
            new("newShell", "New Shell", Key.T, CtrlShiftAlt, () => _store.NewTab(command: "")),
            new("newWorktree", "New Session in Worktree…", Key.G, CtrlShift, () => { if ((_store.SelectedGroup ?? _store.Groups.FirstOrDefault()) is { } group) ShowNewWorktree(group.Id); }),
            new("newProject", "New Project", Key.N, CtrlShift, _store.NewGroup),
            new("openFolder", "Open Folder…", Key.O, CtrlShift, _store.OpenFolderPanel),
            new("recentSessions", "Open Recent Claude Session…", Key.O, CtrlShiftAlt, ShowRecentSessions),
            new("closeSession", "Close Session", Key.W, CtrlShift, () => OnSelected(_store.RequestClose)),
            new("filesPanel", "Right Panel", Key.E, CtrlShift, () => Prefs.Current.ShowFiles = !Prefs.Current.ShowFiles),
            new("panelFiles", "Files", Key.D1, CtrlShift, () => Store.ShowPanel(PanelTab.Files)),
            new("panelChanges", "Changes", Key.D2, CtrlShift, () => Store.ShowPanel(PanelTab.Changes)),
            new("panelBrowser", "Browser", Key.D3, CtrlShift, () => Store.ShowPanel(PanelTab.Browser)),
            new("zoomIn", "Bigger Text", Key.OemPlus, ModifierKeys.Control, () => Zoom(1), Key.Add),
            new("zoomOut", "Smaller Text", Key.OemMinus, ModifierKeys.Control, () => Zoom(-1), Key.Subtract),
            new("zoomReset", "Default Text Size", Key.D0, ModifierKeys.Control, () => Zoom(0)),
            new("rename", "Rename", Key.F2, ModifierKeys.None, () => _store.BeginRename(_store.SelectedTabId)),
            new("editSession", "Edit Session…", Key.I, CtrlShift, () => OnSelected(EditTab)),
            new("restartSession", "Restart Session", Key.R, CtrlShift, () => OnSelected(_store.Restart)),
            new("interrupt", "Interrupt (Ctrl-C)", Key.OemPeriod, CtrlShift, () => OnSelected(_store.Stop)),
            new("killSession", "Kill Session", Key.None, ModifierKeys.None, () => OnSelected(_store.Kill)),
            new("continueClaude", "Continue Claude Conversation", Key.Enter, CtrlShift, () => OnSelected(_store.Start)),
            new("jumpToSession", "Jump to Session…", Key.K, CtrlShift, ShowQuickSwitcher),
            new("find", "Find in Session…", Key.F, CtrlShift, () => { if (_store.SelectedTab is not null) TerminalSurface.Shared.Find(); }),
            new("nextSession", "Next Session", Key.Tab, ModifierKeys.Control, () => _store.SelectRelative(1)),
            new("previousSession", "Previous Session", Key.Tab, CtrlShift, () => _store.SelectRelative(-1)),
            new("settings", "Settings…", Key.OemComma, ModifierKeys.Control, ShowSettings),
            // WebView2 swallows Alt+F4 while a terminal has focus, so omux handles it itself.
            new("quit", "Quit omux", Key.F4, ModifierKeys.Alt, Close),
        };
        for (var n = 1; n <= 9; n++)
        {
            var index = n - 1;
            commands.Add(new($"session{n}", $"Session {n}", Key.D0 + n, ModifierKeys.Control, () => _store.SelectIndex(index)));
        }
        return commands;
    }

    /// <summary>
    /// Runs a command by name. A shortcut can reach us twice (WebView2 forwards accelerator
    /// keys to WPF, and the terminal page reports them too), so a repeat within 150 ms is ignored.
    /// </summary>
    private void Run(string name)
    {
        var now = DateTime.UtcNow;
        if (_lastCommand.Name == name && now - _lastCommand.At < TimeSpan.FromMilliseconds(150)) return;
        _lastCommand = (name, now);
        Commands.FirstOrDefault(c => c.Name == name)?.Run();
    }

    /// <summary>Steps the terminal font size; 0 goes back to the default 13 pt.</summary>
    private static void Zoom(int step) =>
        Prefs.Current.FontSize = step == 0 ? 13 : Math.Clamp(Fonts.TerminalSize + step, 9, 24);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.FocusedElement is TextBox && key == Key.F2) return;
        var command = Commands.FirstOrDefault(c => c.Matches(key, Keyboard.Modifiers));
        if (command is null) return;
        e.Handled = true;
        // Run after the handler returns: a command that opens a modal dialog would otherwise keep
        // WebView2 waiting for this key's Handled flag, and the key would leak into the terminal.
        Dispatcher.BeginInvoke(() => Run(command.Name));
    }

    private void OnStoreChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Store.SelectedTabId):
                UpdateNoSession();
                TrackChanges();
                if (_store.SelectedTab is { IsRenaming: false })
                    Dispatcher.BeginInvoke(TerminalSurface.Shared.FocusTerminal, DispatcherPriority.Input);
                break;
            case nameof(Store.PreviewRequest):
                if (_store.PreviewRequest is not null) Store.ShowPanel(PanelTab.Files);
                break;
        }
    }

    private void OnPrefsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Prefs.Theme):
                App.ApplyTheme();
                _store.ApplyAppearance();
                Right.Files.ApplyTheme();
                foreach (var (tab, _) in _store.AllTabs) tab.RefreshDot();
                break;
            case nameof(Prefs.FontFamily) or nameof(Prefs.FontSize):
                _store.ApplyAppearance();
                break;
            case nameof(Prefs.ShowFolderPath):
                foreach (var (tab, _) in _store.AllTabs) tab.Runtime.Lines();
                break;
            case nameof(Prefs.ShowFiles):
                ApplyFilesVisibility();
                break;
        }
    }

    private void UpdateNoSession()
    {
        var hasSession = _store.SelectedTab is not null;
        NoSession.Visibility = hasSession ? Visibility.Collapsed : Visibility.Visible;
        TerminalHost.Visibility = hasSession ? Visibility.Visible : Visibility.Hidden;
    }

    private void ApplyFilesVisibility()
    {
        var show = Prefs.Current.ShowFiles;
        if (!show && FilesColumn.ActualWidth > 0) _filesWidth = FilesColumn.ActualWidth;
        FilesColumn.MinWidth = show ? 260 : 0;
        FilesColumn.Width = new GridLength(show ? _filesWidth : 0);
        FilesSplitterColumn.Width = new GridLength(show ? 5 : 0);
        FilesSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Right.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FilesToggle.Foreground = (Brush)FindResource(show ? "ForegroundBrush" : "SecondaryBrush");
        Right.SetActive(show);
    }

    private void ToggleFiles_Click(object sender, RoutedEventArgs e) => Prefs.Current.ShowFiles = !Prefs.Current.ShowFiles;

    private void Stop_Click(object sender, RoutedEventArgs e) { if (_store.SelectedTabId is { } id) _store.Stop(id); }

    private void Start_Click(object sender, RoutedEventArgs e) { if (_store.SelectedTabId is { } id) _store.Start(id); }

    /// <summary>Folders from Explorer's "Open in omux", the command line or a second launch.</summary>
    public void OpenFromOutside(IEnumerable<string> folders)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        foreach (var folder in folders) _store.OpenFolder(folder);
    }

    // MARK: Sheets

    public void ShowQuickSwitcher() => new QuickSwitcher { Owner = this }.ShowDialog();

    public void ShowRecentSessions() => new RecentSessionsWindow { Owner = this }.ShowDialog();

    public void EditTab(Guid id) => new TabEditWindow(id) { Owner = this }.ShowDialog();

    public void EditGroup(Guid id) => new GroupEditWindow(id) { Owner = this }.ShowDialog();

    public void ShowSettings() => new SettingsWindow { Owner = this }.ShowDialog();

    public void ShowNewWorktree(Guid groupId) => new WorktreeWindow(groupId) { Owner = this }.ShowDialog();

    // MARK: Taskbar badge

    /// <summary>The taskbar button shows how many sessions wait on the user, like the Dock badge.</summary>
    private void UpdateBadge(int count)
    {
        // A session newly waiting while omux is in the background flashes the taskbar button.
        if (count > _attentionCount && !IsActive) Desktop.FlashTaskbar(this);
        _attentionCount = count;
        if (count == 0)
        {
            Taskbar.Overlay = null;
            return;
        }
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawEllipse(new SolidColorBrush(TabColors.Hex(0xFF3B30)), null, new Point(16, 16), 16, 16);
            var text = new FormattedText(count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                count > 9 ? 15 : 19, Brushes.White, 1.0);
            dc.DrawText(text, new Point(16 - text.Width / 2, 16 - text.Height / 2));
        }
        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Taskbar.Overlay = bitmap;
    }

    // MARK: Quit

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var running = _store.RunningSessionCount;
        if (Prefs.Current.ConfirmQuit && running > 0)
        {
            var answer = MessageBox.Show(this,
                $"{running} session{(running == 1 ? " is" : "s are")} running. Claude sessions resume automatically on next launch.",
                "Quit omux?", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK)
            {
                e.Cancel = true;
                return;
            }
        }
        SavePlacement();
        _store.PrepareForTermination();
    }

    private void RestorePlacement()
    {
        if (Prefs.Current.Window is not { } placement) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!screen.IntersectsWith(new Rect(placement.Left, placement.Top, placement.Width, placement.Height))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top, Width, Height) = (placement.Left, placement.Top, placement.Width, placement.Height);
        if (placement.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        Prefs.Current.Window = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
        Prefs.Current.Save();
    }
}
