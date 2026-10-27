using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Omux;

public partial class QuickSwitcher : Window
{
    public sealed record Match(TabItem Tab, TabGroup Group);

    private readonly Store _store = Store.Shared;

    public QuickSwitcher()
    {
        InitializeComponent();
        Loaded += (_, _) => Query.Focus();
        Deactivated += (_, _) => Dismiss();
        SourceInitialized += (_, _) => Desktop.StyleTitleBar(this, Theme.Current);
        Update();
    }

    private void Update()
    {
        var query = Query.Text;
        Results.ItemsSource = _store.AllTabs
            .Where(e => query.Length == 0 ||
                $"{e.Tab.Name} {e.Group.Name} {e.Tab.Runtime.Title}".Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Take(15)
            .Select(e => new Match(e.Tab, e.Group))
            .ToList();
        Results.SelectedIndex = 0;
    }

    private void Query_TextChanged(object sender, TextChangedEventArgs e) => Update();

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Dismiss(); break;
            case Key.Enter: Pick(Results.SelectedItem as Match); break;
            case Key.Down: Results.SelectedIndex = Math.Min(Results.SelectedIndex + 1, Results.Items.Count - 1); break;
            case Key.Up: Results.SelectedIndex = Math.Max(Results.SelectedIndex - 1, 0); break;
            default: return;
        }
        if (Results.SelectedItem is not null) Results.ScrollIntoView(Results.SelectedItem);
        e.Handled = true;
    }

    private void Results_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Pick(Results.SelectedItem as Match);

    private void Pick(Match? match)
    {
        if (match is null) return;
        _store.SelectedTabId = match.Tab.Id;
        Dismiss();
    }

    private bool _closing;

    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
