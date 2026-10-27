using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Omux;

public partial class RecentSessionsWindow : Window
{
    public sealed record Row(RecentSession Session, bool IsOpen, bool ShowFolder)
    {
        public string Folder => Session.Folder;
        public string When => RelativeDate(Session.Date);
    }

    private readonly Store _store = Store.Shared;
    private List<RecentSession> _sessions = [];
    private bool _loaded;
    private bool _byFolder;
    private bool _closing;

    public RecentSessionsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Desktop.StyleTitleBar(this, Theme.Current);
        Deactivated += (_, _) => Dismiss();
        Loaded += async (_, _) =>
        {
            Query.Focus();
            _sessions = await Task.Run(() => RecentSession.Load());
            _loaded = true;
            Update();
        };
    }

    private void Update()
    {
        if (!_loaded) return;
        var query = Query.Text;
        var openIds = _store.AllTabs.Select(e => e.Tab.ClaudeSessionId).ToHashSet();
        var matches = _sessions
            .Where(s => query.Length == 0 || $"{s.Prompt} {s.Folder}".Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Select(s => new Row(s, openIds.Contains(s.Id), ShowFolder: !_byFolder))
            .ToList();

        Message.Text = !_sessions.Any() ? "No Claude sessions found in ~\\.claude\\projects" : "No matches";
        Message.Visibility = matches.Any() ? Visibility.Collapsed : Visibility.Visible;

        // Folders ordered by their most recent session; sessions stay newest-first within each.
        var view = new ListCollectionView(matches);
        if (_byFolder) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Row.Folder)));
        Results.ItemsSource = view;
        Results.SelectedIndex = matches.Any() ? 0 : -1;
    }

    private void Query_TextChanged(object sender, TextChangedEventArgs e) => Update();

    private void RecentMode_Click(object sender, RoutedEventArgs e) => SetMode(byFolder: false);

    private void FolderMode_Click(object sender, RoutedEventArgs e) => SetMode(byFolder: true);

    private void SetMode(bool byFolder)
    {
        _byFolder = byFolder;
        RecentMode.IsChecked = !byFolder;
        FolderMode.IsChecked = byFolder;
        Update();
        Query.Focus();
    }

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Dismiss(); break;
            case Key.Enter: Pick(Results.SelectedItem as Row); break;
            case Key.Down: Results.SelectedIndex = Math.Min(Results.SelectedIndex + 1, Results.Items.Count - 1); break;
            case Key.Up: Results.SelectedIndex = Math.Max(Results.SelectedIndex - 1, 0); break;
            default: return;
        }
        if (Results.SelectedItem is not null) Results.ScrollIntoView(Results.SelectedItem);
        e.Handled = true;
    }

    private void Results_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Pick(Results.SelectedItem as Row);

    private void Pick(Row? row)
    {
        if (row is null) return;
        Dismiss();
        _store.OpenClaudeSession(row.Session);
    }

    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    /// <summary>"5 minutes ago", "yesterday", like RelativeDateTimeFormatter's named style.</summary>
    public static string RelativeDate(DateTime date)
    {
        var elapsed = DateTime.Now - date;
        if (elapsed.TotalMinutes < 1) return "now";
        if (elapsed.TotalHours < 1) return Plural((int)elapsed.TotalMinutes, "minute") + " ago";
        if (elapsed.TotalDays < 1) return Plural((int)elapsed.TotalHours, "hour") + " ago";
        if (date.Date == DateTime.Today.AddDays(-1)) return "yesterday";
        if (elapsed.TotalDays < 7) return Plural((int)Math.Ceiling(elapsed.TotalDays), "day") + " ago";
        if (elapsed.TotalDays < 31) return Plural((int)(elapsed.TotalDays / 7), "week") + " ago";
        if (elapsed.TotalDays < 365) return Plural((int)(elapsed.TotalDays / 30), "month") + " ago";
        return Plural((int)(elapsed.TotalDays / 365), "year") + " ago";
    }

    private static string Plural(int count, string unit) => count == 1 ? $"1 {unit}" : $"{count} {unit}s";
}
