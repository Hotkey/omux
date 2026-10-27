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

/// <summary>A menu command and its keyboard shortcut (⌘ on macOS becomes Ctrl+Shift here).</summary>
public sealed record AppCommand(string Name, string Title, Key Key, ModifierKeys Modifiers, Action Run)
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
            parts.Add(Key switch
            {
                >= Key.D0 and <= Key.D9 => ((int)(Key - Key.D0)).ToString(CultureInfo.InvariantCulture),
                Key.OemPeriod => ".",
                Key.OemComma => ",",
                Key.Enter => "Enter",
                _ => Key.ToString(),
            });
            return string.Join('+', parts);
        }
    }

    /// <summary>The DOM KeyboardEvent.code, so the terminal page can hand the shortcut back.</summary>
    public string? Code => Key switch
    {
        >= Key.A and <= Key.Z => "Key" + Key,
        >= Key.D0 and <= Key.D9 => "Digit" + (int)(Key - Key.D0),
        >= Key.F1 and <= Key.F12 => Key.ToString(),
        Key.OemPeriod => "Period",
        Key.OemComma => "Comma",
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
    private double _filesWidth = 340;

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
        TerminalSurface.Shared.SetShortcuts(Commands.Where(c => c.Code is not null).Select(c => new
        {
            name = c.Name,
            code = c.Code,
            ctrl = c.Modifiers.HasFlag(ModifierKeys.Control),
            shift = c.Modifiers.HasFlag(ModifierKeys.Shift),
            alt = c.Modifiers.HasFlag(ModifierKeys.Alt),
        }));
        _store.Load();
        Files.Initialize(WebEnvironment);
        OpenFromOutside(_launchFolders);
        UpdateNoSession();
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
            new("newProject", "New Project", Key.N, CtrlShift, _store.NewGroup),
            new("openFolder", "Open Folder…", Key.O, CtrlShift, _store.OpenFolderPanel),
            new("recentSessions", "Open Recent Claude Session…", Key.O, CtrlShiftAlt, ShowRecentSessions),
            new("closeSession", "Close Session", Key.W, CtrlShift, () => OnSelected(_store.CloseTab)),
            new("filesPanel", "Files Panel", Key.F, CtrlShift, () => Prefs.Current.ShowFiles = !Prefs.Current.ShowFiles),
            new("rename", "Rename", Key.F2, ModifierKeys.None, () => _store.BeginRename(_store.SelectedTabId)),
            new("editSession", "Edit Session…", Key.I, CtrlShift, () => OnSelected(EditTab)),
            new("restartSession", "Restart Session", Key.R, CtrlShift, () => OnSelected(_store.Restart)),
            new("interrupt", "Interrupt (Ctrl-C)", Key.OemPeriod, CtrlShift, () => OnSelected(_store.Stop)),
            new("killSession", "Kill Session", Key.None, ModifierKeys.None, () => OnSelected(_store.Kill)),
            new("continueClaude", "Continue Claude Conversation", Key.Enter, CtrlShift, () => OnSelected(_store.Start)),
            new("jumpToSession", "Jump to Session…", Key.K, CtrlShift, ShowQuickSwitcher),
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

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.FocusedElement is TextBox && key == Key.F2) return;
        var command = Commands.FirstOrDefault(c => c.Key != Key.None && c.Key == key && c.Modifiers == Keyboard.Modifiers);
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
                if (_store.SelectedTab is { IsRenaming: false })
                    Dispatcher.BeginInvoke(TerminalSurface.Shared.FocusTerminal, DispatcherPriority.Input);
                break;
            case nameof(Store.PreviewRequest):
                if (_store.PreviewRequest is not null) Prefs.Current.ShowFiles = true;
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
                Files.ApplyTheme();
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
        FilesColumn.MinWidth = show ? 240 : 0;
        FilesColumn.Width = new GridLength(show ? _filesWidth : 0);
        FilesSplitterColumn.Width = new GridLength(show ? 5 : 0);
        FilesSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Files.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FilesToggle.Foreground = (Brush)FindResource(show ? "ForegroundBrush" : "SecondaryBrush");
        Files.SetActive(show);
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

    // MARK: Taskbar badge

    /// <summary>The taskbar button shows how many sessions wait on the user, like the Dock badge.</summary>
    private void UpdateBadge(int count)
    {
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
