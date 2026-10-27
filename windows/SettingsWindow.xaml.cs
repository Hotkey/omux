using System.Windows;

namespace Omux;

/// <summary>Settings apply as soon as they change, like SwiftUI's @AppStorage.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = Prefs.Current;
        SourceInitialized += (_, _) => Desktop.StyleTitleBar(this, Theme.Current);
        ThemeBox.ItemsSource = Theme.All;
        FontBox.ItemsSource = new[] { new { Label = "System Mono", Value = "" } }
            .Concat(Fonts.MonospaceFamilies.Select(f => new { Label = f, Value = f }))
            .ToList();
        ExplorerMenu.IsChecked = Desktop.ExplorerMenuInstalled;
        ShellBox.TextChanged += (_, _) => UpdateShellPrompt();
        UpdateShellPrompt();
    }

    private void UpdateShellPrompt() => ShellPrompt.Visibility = ShellBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ExplorerMenu_Click(object sender, RoutedEventArgs e)
    {
        try { Desktop.ExplorerMenuInstalled = ExplorerMenu.IsChecked == true; }
        catch (Exception error) { MessageBox.Show(this, error.Message, "omux", MessageBoxButton.OK, MessageBoxImage.Warning); }
        ExplorerMenu.IsChecked = Desktop.ExplorerMenuInstalled;
    }
}
