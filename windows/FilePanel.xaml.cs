using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Omux;

/// <summary>Browses the session's folder and previews the selected file.</summary>
public partial class FilePanel : UserControl
{
    private readonly Store _store = Store.Shared;
    private readonly ObservableCollection<FileNode> _sections = [];
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _scanTimer;
    private FileSystemWatcher? _watcher;
    private FileNode? _rootSection;
    private string? _root;
    private string? _previewPath;
    private bool _active;
    private bool _webReady;

    public FilePanel()
    {
        InitializeComponent();
        Tree.ItemsSource = _sections;
        // Folder changes are coalesced to one refresh per second.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); Refresh(); };
        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _scanTimer.Tick += (_, _) => _store.ScanMentionedPaths();

        _store.PropertyChanged += OnStoreChanged;
        _store.MentionedPaths.CollectionChanged += (_, _) => RebuildSections();
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths && File.Exists(paths[0])) Preview(paths[0]);
            e.Handled = true;
        };
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        ApplyBrowserCollapsed();
        ApplySort();
    }

    public async void Initialize(CoreWebView2Environment environment)
    {
        await PreviewWeb.EnsureCoreWebView2Async(environment);
        var core = PreviewWeb.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.AddWebResourceRequestedFilter(PreviewPage.Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(PreviewPage.Html));
            e.Response = environment.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: text/html; charset=utf-8");
        };
        core.NavigationStarting += OnPreviewNavigating;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            Desktop.Open(e.Uri);
        };
        _webReady = true;
        if (_previewPath is { } path) Preview(path);
    }

    /// <summary>The panel scans for mentioned paths and watches its folder only while shown.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (!active)
        {
            _scanTimer.Stop();
            return;
        }
        UpdateRoot();
        ShowRequested();
        _store.ScanMentionedPaths();
        _scanTimer.Start();
    }

    public void ApplyTheme()
    {
        if (_previewPath is { } path) Preview(path);
    }

    private void OnStoreChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Store.OpenAgent) or nameof(Store.SelectedTabId)) ShowAgent();
        if (!_active) return;
        switch (e.PropertyName)
        {
            case nameof(Store.SelectedTabId):
                UpdateRoot();
                _store.ScanMentionedPaths();
                break;
            case nameof(Store.PreviewRequest):
                ShowRequested();
                break;
        }
    }

    /// <summary>The open subagent's transcript, when it belongs to the selected session, replaces the preview.</summary>
    private void ShowAgent()
    {
        var agent = _store.OpenAgent is { } open && open.TabId == _store.SelectedTabId ? open : null;
        if (agent is null)
        {
            AgentHost.Content = null;
            AgentHost.Visibility = Visibility.Collapsed;
            PreviewArea.Visibility = Visibility.Visible;
            return;
        }
        if (AgentHost.Content is not AgentTranscriptView { } view || view.AgentId != agent.AgentId)
            AgentHost.Content = new AgentTranscriptView(agent);
        AgentHost.Visibility = Visibility.Visible;
        // The preview is a WebView2, which would draw over anything placed on top of it.
        PreviewArea.Visibility = Visibility.Collapsed;
    }

    /// <summary>Folder the panel shows: the selected session's folder, else its project's.</summary>
    private void UpdateRoot()
    {
        var path = Paths.Expand(_store.SelectedTab?.Directory ?? _store.SelectedGroup?.Directory ?? Paths.Home);
        if (!Directory.Exists(path)) path = Paths.Home;
        if (Paths.Same(path, _root)) return;
        _root = path;
        RootName.Text = Paths.LastComponent(path);
        _rootSection = null;
        ClearPreview();
        Refresh();

        _watcher?.Dispose();
        try
        {
            _watcher = new FileSystemWatcher(path) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
            FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() => { if (!_refreshTimer.IsEnabled) _refreshTimer.Start(); });
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Changed += changed;
            _watcher.Renamed += (s, e) => changed(s, e);
            _watcher.EnableRaisingEvents = true;
        }
        catch { _watcher = null; }
    }

    private void Refresh()
    {
        if (_root is null) return;
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _rootSection?.CollectExpanded(expanded);
        var section = FileNode.Section(Paths.LastComponent(_root), []);
        foreach (var node in FileNode.List(_root))
        {
            if (expanded.Contains(node.Path)) node.Restore(expanded);
            section.Children.Add(node);
        }
        _rootSection = section;
        RebuildSections();
    }

    private void RebuildSections()
    {
        _sections.Clear();
        if (_store.MentionedPaths.Any())
        {
            var mentioned = _store.MentionedPaths.Reverse().Select(path => new FileNode(path, false)
            {
                Subtitle = Paths.Abbreviate(Path.GetDirectoryName(path) ?? ""),
            });
            _sections.Add(FileNode.Section("Mentioned in session", mentioned));
        }
        if (_rootSection is not null) _sections.Add(_rootSection);
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not FileNode { IsDirectory: false, IsSection: false } node || !File.Exists(node.Path)) return;
        _store.OpenAgent = null;
        Preview(node.Path);
    }

    private void ShowRequested()
    {
        if (_store.PreviewRequest is not { } path) return;
        _store.PreviewRequest = null;
        _store.OpenAgent = null;
        Preview(path);
    }

    // MARK: Preview

    private void Preview(string path)
    {
        _previewPath = path;
        PreviewHeader.Visibility = Visibility.Visible;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        PreviewName.Text = Path.GetFileName(path);
        PreviewSize.Text = File.Exists(path) ? PreviewPage.FormatSize(new FileInfo(path).Length) : "";
        var isText = PreviewPage.IsText(path);
        WrapButton.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
        WrapButton.ToolTip = Prefs.Current.PreviewWrapLines ? "Don't wrap lines" : "Wrap lines";
        WrapButton.Foreground = (Brush)FindResource(Prefs.Current.PreviewWrapLines ? "AccentBrush" : "SecondaryBrush");
        if (!_webReady) return;

        PreviewWeb.Visibility = Visibility.Visible;
        var core = PreviewWeb.CoreWebView2;
        // The file's folder is served as https://omux.preview/, so relative images in Markdown resolve.
        core.ClearVirtualHostNameToFolderMapping(PreviewPage.FilesHost);
        core.SetVirtualHostNameToFolderMapping(PreviewPage.FilesHost, Path.GetDirectoryName(path)!, CoreWebView2HostResourceAccessKind.Allow);
        PreviewPage.Html = PreviewPage.Render(path, Theme.Current, Prefs.Current.PreviewWrapLines);
        core.Navigate(PreviewPage.UrlFor(path));
    }

    private void ClearPreview()
    {
        _previewPath = null;
        PreviewHeader.Visibility = Visibility.Collapsed;
        PreviewWeb.Visibility = Visibility.Collapsed;
        PreviewPlaceholder.Visibility = Visibility.Visible;
    }

    /// <summary>Links in a preview: other local files preview here, everything else opens outside.</summary>
    private void OnPreviewNavigating(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            e.Cancel = true;
            return;
        }
        if (uri.Host == PreviewPage.PageHost) return;
        if (uri.Host == PreviewPage.FilesHost && _previewPath is { } shown)
        {
            var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shown)!, Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))));
            // The previewed file itself (a PDF opens in the browser's viewer).
            if (Paths.Same(target, shown)) return;
            e.Cancel = true;
            if (File.Exists(target)) Dispatcher.BeginInvoke(() => Preview(target));
            return;
        }
        e.Cancel = true;
        Desktop.Open(e.Uri);
    }

    private void Wrap_Click(object sender, RoutedEventArgs e)
    {
        Prefs.Current.PreviewWrapLines = !Prefs.Current.PreviewWrapLines;
        if (_previewPath is { } path) Preview(path);
    }

    private void OpenWith_Click(object sender, RoutedEventArgs e) { if (_previewPath is { } path) Desktop.Open(path); }

    private void Reveal_Click(object sender, RoutedEventArgs e) { if (_previewPath is { } path) Desktop.RevealInExplorer(path); }

    // MARK: Browser

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        Prefs.Current.FilesBrowserCollapsed = !Prefs.Current.FilesBrowserCollapsed;
        ApplyBrowserCollapsed();
    }

    private void ApplyBrowserCollapsed()
    {
        var collapsed = Prefs.Current.FilesBrowserCollapsed;
        BrowserRow.MinHeight = collapsed ? 28 : 80;
        BrowserRow.MaxHeight = collapsed ? 28 : double.PositiveInfinity;
        BrowserRow.Height = collapsed ? new GridLength(28) : new GridLength(2, GridUnitType.Star);
        Splitter.IsEnabled = !collapsed;
        Tree.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SortPicker.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseButton.RenderTransform = new RotateTransform(collapsed ? 0 : 90);
        CollapseButton.ToolTip = collapsed ? "Show files" : "Hide files";
    }

    private void SortName_Click(object sender, RoutedEventArgs e) => SetSort(byDate: false);

    private void SortDate_Click(object sender, RoutedEventArgs e) => SetSort(byDate: true);

    private void SetSort(bool byDate)
    {
        Prefs.Current.FilesByDate = byDate;
        ApplySort();
        Refresh();
    }

    private void ApplySort()
    {
        SortName.IsChecked = !Prefs.Current.FilesByDate;
        SortDate.IsChecked = Prefs.Current.FilesByDate;
    }

    private void OpenRoot_Click(object sender, RoutedEventArgs e) { if (_root is { } root) Desktop.Open(root); }

    private void Node_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileNode { IsSection: false } node) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        menu.Items.Add(new MenuItem { Header = "Open", Command = new Relay(() => Desktop.Open(node.Path)) });
        menu.Items.Add(new MenuItem { Header = "Show in Explorer", Command = new Relay(() => Desktop.RevealInExplorer(node.Path)) });
        menu.Items.Add(new MenuItem { Header = "Copy Path", Command = new Relay(() => Clipboard.SetText(node.Path)) });
        menu.IsOpen = true;
    }
}
