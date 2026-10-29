using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Omux;

/// <summary>The right panel: Files, Changes and Browser for the selected session.</summary>
public partial class RightPanel : UserControl
{
    private readonly Store _store = Store.Shared;
    private bool _active;

    public RightPanel()
    {
        InitializeComponent();
        Prefs.Current.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Prefs.PanelTab)) ApplyTab(); };
        Changes.Shared.PropertyChanged += OnChangesChanged;
        _store.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Store.SelectedTabId)) UpdateBadge(); };
        ApplyTab();
    }

    /// <summary>The panel works (scans output, watches folders, loads pages) only while shown.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        Files.SetActive(active);
        ApplyTab();
    }

    private void ApplyTab()
    {
        var tab = Prefs.Current.PanelTab;
        Files.Visibility = tab == PanelTab.Files ? Visibility.Visible : Visibility.Collapsed;
        ChangesView.Visibility = tab == PanelTab.Changes ? Visibility.Visible : Visibility.Collapsed;
        BrowserView.Visibility = tab == PanelTab.Browser ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (button, value) in new[] { (FilesTab, PanelTab.Files), (ChangesTab, PanelTab.Changes), (BrowserTab, PanelTab.Browser) })
        {
            if (tab == value)
            {
                button.SetResourceReference(BackgroundProperty, "SelectionBrush");
                button.SetResourceReference(ForegroundProperty, "ForegroundBrush");
            }
            else
            {
                button.Background = Brushes.Transparent;
                button.ClearValue(ForegroundProperty);
            }
        }
        if (_active && tab == PanelTab.Changes) ChangesView.Activate();
        BrowserView.SetActive(_active && tab == PanelTab.Browser);
    }

    private void OnChangesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Changes.Files) or nameof(Changes.TabId)) UpdateBadge();
    }

    private void UpdateBadge()
    {
        var count = Changes.Shared.TabId == _store.SelectedTabId ? Changes.Shared.Files.Count : 0;
        ChangesBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChangesCount.Text = count.ToString();
    }

    private void FilesTab_Click(object sender, RoutedEventArgs e) => Prefs.Current.PanelTab = PanelTab.Files;
    private void ChangesTab_Click(object sender, RoutedEventArgs e) => Prefs.Current.PanelTab = PanelTab.Changes;
    private void BrowserTab_Click(object sender, RoutedEventArgs e) => Prefs.Current.PanelTab = PanelTab.Browser;
}
