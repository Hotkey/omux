using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Omux;

/// <summary>One row of the diff, with the note under it (or its editor).</summary>
public sealed class DiffRow : Observable
{
    private DiffNote? _note;
    private bool _isEditing;
    private string _draft = "";

    public DiffRow(DiffLine line, TextWrapping wrapping)
    {
        Line = line;
        Wrapping = wrapping;
    }

    public DiffLine Line { get; }
    public TextWrapping Wrapping { get; }
    public string NumberText => Line.Number?.ToString() ?? "";
    public string Text => Line.Text.Length == 0 ? " " : Line.Text;
    public bool IsMeta => Line.Kind is DiffKind.Hunk or DiffKind.Info;
    public bool CanNote => !IsMeta;

    public string Sign => Line.Kind switch { DiffKind.Added => "+", DiffKind.Removed => "−", _ => "" };

    public Brush SignBrush => Line.Kind == DiffKind.Removed ? Red : Green;

    public Brush Background => Line.Kind switch
    {
        DiffKind.Added => AddedBackground,
        DiffKind.Removed => RemovedBackground,
        DiffKind.Hunk => HunkBackground,
        _ => Brushes.Transparent,
    };

    public DiffNote? Note { get => _note; set { if (Set(ref _note, value)) Raise(nameof(NoteVisible)); } }
    public bool IsEditing { get => _isEditing; set { if (Set(ref _isEditing, value)) Raise(nameof(NoteVisible)); } }
    public string Draft { get => _draft; set => Set(ref _draft, value); }
    public bool NoteVisible => Note is not null && !IsEditing;

    private static readonly Brush Green = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
    private static readonly Brush Red = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));
    private static readonly Brush AddedBackground = Frozen(Color.FromArgb(0x21, 0x30, 0xD1, 0x58));
    private static readonly Brush RemovedBackground = Frozen(Color.FromArgb(0x21, 0xFF, 0x45, 0x3A));
    private static readonly Brush HunkBackground = Frozen(Color.FromArgb(0x14, 0x0A, 0x84, 0xFF));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

public sealed record FileRow(ChangedFile File, bool HasNote);

/// <summary>The Changes tab: the session's diff, with notes for Claude on any line.</summary>
public partial class ChangesPanel : UserControl
{
    private readonly Changes _changes = Changes.Shared;
    private readonly Store _store = Store.Shared;
    private List<DiffRow> _rows = [];
    private string? _selected;
    private TabItem? _watched;
    private bool _rebuilding;
    private int _reloads;

    public ChangesPanel()
    {
        InitializeComponent();
        _changes.PropertyChanged += OnChangesChanged;
        _changes.NotesChanged += () =>
        {
            ApplyNotes();
            RebuildFiles();
            UpdateFooter();
        };
        _store.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Store.SelectedTabId)) WatchSelected(); };
        Prefs.Current.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Prefs.PreviewWrapLines)) Reload(); };
        WatchSelected();
        UpdateState();
    }

    /// <summary>Called when the tab comes into view, so the list is fresh.</summary>
    public void Activate() => _changes.Refresh();

    private Worktree? Worktree => _store.SelectedTab?.Worktree;
    private ChangedFile? File => _changes.Files.FirstOrDefault(f => f.Path == _selected);

    private void OnChangesChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Changes.Files):
                PickFile();
                RebuildFiles();
                Reload();
                UpdateState();
                break;
            case nameof(Changes.TabId):
                _selected = null;
                PickFile();
                break;
            case nameof(Changes.IsRepo) or nameof(Changes.BaseName):
                UpdateState();
                break;
        }
    }

    /// <summary>Follows the selected session's git activity ("Pushing…") for the footer.</summary>
    private void WatchSelected()
    {
        if (_watched is not null) _watched.Runtime.PropertyChanged -= OnRuntimeChanged;
        _watched = _store.SelectedTab;
        if (_watched is not null) _watched.Runtime.PropertyChanged += OnRuntimeChanged;
        UpdateFooter();
    }

    private void OnRuntimeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabRuntime.GitActivity)) UpdateFooter();
    }

    private void UpdateState()
    {
        var files = _changes.Files.Count;
        Body.Visibility = _changes.IsRepo && files > 0 ? Visibility.Visible : Visibility.Collapsed;
        Placeholder.Visibility = Body.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        Footer.Visibility = _changes.IsRepo ? Visibility.Visible : Visibility.Collapsed;
        if (!_changes.IsRepo)
        {
            PlaceholderTitle.Text = "Not a Git repository";
            PlaceholderDetail.Text = "Changes show up for sessions inside a Git repository.";
        }
        else
        {
            PlaceholderTitle.Text = "No changes";
            PlaceholderDetail.Text = $"Nothing differs from {_changes.BaseName} yet.";
        }
        FileCount.Text = $"{files} changed file{(files == 1 ? "" : "s")}";
        BaseName.Text = $"vs {_changes.BaseName}";
        UpdateFooter();
    }

    private void UpdateFooter()
    {
        var notes = _changes.Notes.Count;
        NotesBar.Visibility = notes > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoteCount.Text = $"{notes} note{(notes == 1 ? "" : "s")}";
        Hint.Visibility = notes == 0 && _changes.Files.Any() ? Visibility.Visible : Visibility.Collapsed;

        var activity = _store.SelectedTab?.Runtime.GitActivity;
        Activity.Text = activity ?? "";
        Activity.Visibility = activity is null ? Visibility.Collapsed : Visibility.Visible;
        GitActions.Visibility = activity is null && Worktree is not null ? Visibility.Visible : Visibility.Collapsed;
        if (Worktree is { } worktree) CreatePr.ToolTip = $"Push {worktree.Branch} and open a pull request into {worktree.Base}";
    }

    // MARK: Files

    private void PickFile()
    {
        if (_selected is null || !_changes.Files.Any(f => f.Path == _selected)) _selected = _changes.Files.FirstOrDefault()?.Path;
    }

    private void RebuildFiles()
    {
        _rebuilding = true;
        var notes = _changes.Notes;
        var rows = _changes.Files.Select(f => new FileRow(f, notes.Any(n => n.Path == f.Path))).ToList();
        FileList.ItemsSource = rows;
        FileList.SelectedItem = rows.FirstOrDefault(r => r.File.Path == _selected);
        _rebuilding = false;
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding || FileList.SelectedItem is not FileRow row || row.File.Path == _selected) return;
        _selected = row.File.Path;
        Reload();
    }

    private void File_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileRow row || Path(row.File) is not { } path) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        menu.Items.Add(new MenuItem { Header = "Open", Command = new Relay(() => Desktop.Open(path)) });
        menu.Items.Add(new MenuItem { Header = "Show in Explorer", Command = new Relay(() => Desktop.RevealInExplorer(path)) });
        menu.Items.Add(new MenuItem { Header = "Copy Path", Command = new Relay(() => Clipboard.SetText(path)) });
        menu.IsOpen = true;
    }

    private string? Path(ChangedFile file) => _changes.Dir is { } dir ? System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, file.Path)) : null;

    private void Refresh_Click(object sender, RoutedEventArgs e) => _changes.Refresh();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) { if (_changes.Dir is { } dir) Desktop.Open(dir); }

    private void OpenFile_Click(object sender, RoutedEventArgs e) { if (File is { } file && Path(file) is { } path && System.IO.File.Exists(path)) Desktop.Open(path); }

    // MARK: Diff

    private async void Reload()
    {
        var reload = ++_reloads;
        var file = File;
        FileName.Text = file?.Name ?? "";
        FileFolder.Text = file?.Folder ?? "";
        FileStat.Content = file;
        if (file is null)
        {
            _rows = [];
            DiffList.ItemsSource = _rows;
            return;
        }
        var lines = await _changes.Diff(file);
        if (reload != _reloads) return;
        var wrapping = Prefs.Current.PreviewWrapLines ? TextWrapping.Wrap : TextWrapping.NoWrap;
        DiffList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, wrapping == TextWrapping.Wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        _rows = lines.Select(line => new DiffRow(line, wrapping)).ToList();
        ApplyNotes();
        DiffList.ItemsSource = _rows;
    }

    private void ApplyNotes()
    {
        var notes = _changes.Notes.Where(n => n.Path == _selected).ToDictionary(n => n.LineIndex);
        foreach (var row in _rows) row.Note = notes.GetValueOrDefault(row.Line.Index);
    }

    private void Line_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DiffRow { CanNote: true } row) BeginEdit(row);
    }

    private void Note_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindButton(source)) return;
        if ((sender as FrameworkElement)?.DataContext is DiffRow row) BeginEdit(row);
    }

    private static bool FindButton(DependencyObject source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is Button) return true;
        return false;
    }

    private void BeginEdit(DiffRow row)
    {
        foreach (var other in _rows.Where(r => r.IsEditing && r != row)) other.IsEditing = false;
        row.Draft = row.Note?.Text ?? "";
        row.IsEditing = true;
    }

    private void SaveNote(DiffRow row)
    {
        if (_selected is { } path) _changes.SetNote(row.Draft, row.Line, path);
        row.IsEditing = false;
    }

    private void SaveNote_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is DiffRow row) SaveNote(row); }

    private void CancelNote_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is DiffRow row) row.IsEditing = false; }

    private void RemoveNote_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is DiffRow { Note: { } note }) _changes.RemoveNote(note); }

    private void NoteBox_KeyDown(object sender, KeyEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DiffRow row) return;
        if (e.Key == Key.Enter) SaveNote(row);
        else if (e.Key == Key.Escape) row.IsEditing = false;
        else return;
        e.Handled = true;
    }

    private void NoteBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box) Dispatcher.BeginInvoke(() => { box.Focus(); box.CaretIndex = box.Text.Length; });
    }

    // MARK: Footer

    private void Send_Click(object sender, RoutedEventArgs e) => _changes.SendNotes();

    private void CreatePr_Click(object sender, RoutedEventArgs e) { if (_store.SelectedTabId is { } id) _store.CreatePullRequest(id); }

    private void MoreGit_Click(object sender, RoutedEventArgs e)
    {
        if (_store.SelectedTabId is not { } id || Worktree is not { } worktree) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, FontFamily = FontFamily };
        menu.Items.Add(new MenuItem { Header = $"Merge into {worktree.Base}", Command = new Relay(() => _store.MergeWorktree(id)) });
        menu.IsOpen = true;
    }
}
