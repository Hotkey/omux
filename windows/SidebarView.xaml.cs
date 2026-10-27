using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Omux;

public partial class SidebarView : UserControl
{
    private const string RowFormat = "omux.sidebar-row";

    private readonly Store _store = Store.Shared;
    private readonly UsageMonitor _usage = new();
    private Point? _dragStart;
    private object? _dragRow;
    private bool _syncing;

    public SidebarView()
    {
        InitializeComponent();
        Usage.DataContext = _usage;
        Loaded += (_, _) => _usage.Start();
        _store.RowsRebuilt += SyncSelection;
        _store.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Store.SelectedTabId)) SyncSelection(); };
    }

    private static object? Item(object sender) => (sender as FrameworkElement)?.DataContext;

    // MARK: Selection

    /// <summary>Mirrors the store's selection into the list; project rows are never selected.</summary>
    private void SyncSelection()
    {
        _syncing = true;
        List.SelectedItem = _store.SelectedTab;
        if (List.SelectedItem is not null) List.ScrollIntoView(List.SelectedItem);
        _syncing = false;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (List.SelectedItem is TabItem tab) _store.SelectedTabId = tab.Id;
        else SyncSelection();
    }

    private void Row_MouseEnter(object sender, MouseEventArgs e)
    {
        if (Item(sender) is TabItem tab) _store.RefreshBusy(tab.Id);
    }

    // MARK: Buttons

    private void NewTab_Click(object sender, RoutedEventArgs e) => _store.NewTab();
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _store.OpenFolderPanel();
    private void Recent_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.ShowRecentSessions();
    private void Settings_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.ShowSettings();
    private void Stop_Click(object sender, RoutedEventArgs e) { if (Item(sender) is TabItem tab) _store.Stop(tab.Id); }
    private void Start_Click(object sender, RoutedEventArgs e) { if (Item(sender) is TabItem tab) _store.Start(tab.Id); }
    private void Collapse_Click(object sender, RoutedEventArgs e) { if (Item(sender) is TabGroup group) _store.ToggleCollapsed(group.Id); }
    private void GroupNewTab_Click(object sender, RoutedEventArgs e) { if (Item(sender) is TabGroup group) _store.NewTab(group.Id); }

    /// <summary>Every command with its shortcut — the menu bar of the macOS app.</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        string? section = null;
        foreach (var command in MainWindow.Instance.Commands)
        {
            var group = command.Name switch
            {
                "newSession" or "newShell" or "newProject" or "openFolder" or "recentSessions" => "file",
                "closeSession" or "filesPanel" or "settings" or "quit" => "window",
                _ => "session",
            };
            if (section is not null && group != section) menu.Items.Add(new Separator());
            section = group;
            menu.Items.Add(new MenuItem { Header = command.Title, InputGestureText = command.Gesture, Command = new Relay(command.Run) });
        }
        menu.IsOpen = true;
    }

    // MARK: Context menus

    private void Row_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = Item(sender) switch
        {
            TabItem tab => TabMenu(tab),
            TabGroup group => GroupMenu(group),
            _ => null,
        };
        if (menu is null) return;
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Empty-space menu; rows have their own.</summary>
    private void List_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = List };
        menu.Items.Add(Entry("New Project", _store.NewGroup));
        menu.Items.Add(Entry("Open Folder as Project…", _store.OpenFolderPanel));
        menu.Items.Add(Entry("Open Recent Claude Session…", MainWindow.Instance.ShowRecentSessions));
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("New Session", () => _store.NewTab()));
        menu.Items.Add(Entry("New Shell", () => _store.NewTab(command: "")));
        menu.IsOpen = true;
    }

    private ContextMenu TabMenu(TabItem tab)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Entry("Rename", () => _store.BeginRename(tab.Id)));
        menu.Items.Add(Entry("Edit Session…", () => MainWindow.Instance.EditTab(tab.Id)));
        var colors = new MenuItem { Header = "Color" };
        foreach (var color in Enum.GetValues<TabColor>())
        {
            colors.Items.Add(new MenuItem
            {
                Header = color.Label(),
                IsChecked = tab.Color == color,
                Icon = color.Color() is { } c ? new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(c) } : null,
                Command = new Relay(() => { tab.Color = color; _store.Touch(); }),
            });
        }
        menu.Items.Add(colors);
        var others = _store.Groups.Where(g => g != _store.GroupContaining(tab.Id)).ToList();
        if (others.Any())
        {
            var moveTo = new MenuItem { Header = "Move to" };
            foreach (var group in others) moveTo.Items.Add(Entry(group.Name, () => _store.MoveTab(tab.Id, group.Id)));
            menu.Items.Add(moveTo);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("Interrupt (Ctrl-C)", () => _store.Stop(tab.Id)));
        menu.Items.Add(Entry("Restart Session", () => _store.Restart(tab.Id)));
        menu.Items.Add(Entry("Kill Session", () => _store.Kill(tab.Id)));
        menu.Items.Add(Entry("Close Session", () => _store.CloseTab(tab.Id)));
        return menu;
    }

    private ContextMenu GroupMenu(TabGroup group)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Entry("New Session", () => _store.NewTab(group.Id)));
        menu.Items.Add(Entry("New Shell", () => _store.NewTab(group.Id, command: "")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("Rename", () => _store.BeginRename(group.Id)));
        menu.Items.Add(Entry("Edit Project…", () => MainWindow.Instance.EditGroup(group.Id)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("Move Up", () => _store.MoveGroup(group.Id, -1), _store.Groups.FirstOrDefault() != group));
        menu.Items.Add(Entry("Move Down", () => _store.MoveGroup(group.Id, 1), _store.Groups.LastOrDefault() != group));
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("Delete Project", () => _store.DeleteGroup(group.Id)));
        return menu;
    }

    private static MenuItem Entry(string title, Action action, bool enabled = true) =>
        new() { Header = title, Command = new Relay(action), IsEnabled = enabled };

    // MARK: Inline rename — commits on Enter or focus loss, cancels on Escape.

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsVisible) return;
        box.Text = Item(box) switch { TabItem tab => tab.Name, TabGroup group => group.Name, _ => box.Text };
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            box.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        FinishRename((TextBox)sender, save: e.Key == Key.Enter);
        e.Handled = true;
    }

    private void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => FinishRename((TextBox)sender, save: true);

    private void FinishRename(TextBox box, bool save)
    {
        var id = Item(box) switch { TabItem tab when tab.IsRenaming => tab.Id, TabGroup group when group.IsRenaming => group.Id, _ => (Guid?)null };
        if (id is not { } renamed) return;
        _store.BeginRename(null);
        if (save) _store.Rename(renamed, box.Text);
        TerminalSurface.Shared.FocusTerminal();
    }

    // MARK: Drag to reorder projects and move sessions between them

    private void Row_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null and not ListBoxItem)
        {
            if (source is ButtonBase or TextBox) return;
            source = VisualTreeHelper.GetParent(source);
        }
        _dragStart = e.GetPosition(List);
        _dragRow = Item(sender);
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start || _dragRow is null) return;
        var delta = e.GetPosition(List) - start;
        if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        var row = _dragRow;
        _dragStart = null;
        _dragRow = null;
        DragDrop.DoDragDrop(List, new DataObject(RowFormat, row), DragDropEffects.Move);
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(RowFormat)) return; // folders bubble up to the window
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void List_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(RowFormat) is not { } row) return;
        e.Handled = true;
        var destination = _store.Rows.Count;
        for (var i = 0; i < _store.Rows.Count; i++)
        {
            if (List.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container) continue;
            var position = e.GetPosition(container);
            if (position.Y < 0 || position.Y > container.ActualHeight) continue;
            destination = position.Y < container.ActualHeight / 2 ? i : i + 1;
            break;
        }
        _store.MoveRow(row, destination);
    }

    // MARK: Usage

    private void Limit_MouseEnter(object sender, MouseEventArgs e) { if (Item(sender) is UsageMonitor.Limit limit) limit.Hovered = true; }
    private void Limit_MouseLeave(object sender, MouseEventArgs e) { if (Item(sender) is UsageMonitor.Limit limit) limit.Hovered = false; }
}

/// <summary>An ICommand for menu items.</summary>
public sealed class Relay(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}
