using System.Windows;
using System.Windows.Controls;

namespace Omux;

public partial class TabEditWindow : Window
{
    private enum CommandMode { Inherit, Shell, Custom }

    private readonly Store _store = Store.Shared;
    private readonly Guid _tabId;
    private Guid _sessionId;

    public TabEditWindow(Guid tabId)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Desktop.StyleTitleBar(this, Theme.Current);
        _tabId = tabId;
        if (_store.Tab(tabId) is not { } tab) return;
        var group = _store.GroupContaining(tabId);

        NameBox.Text = tab.Name;
        FolderBox.Text = tab.Directory ?? "";
        FolderPrompt.Text = group?.Directory ?? "~";
        FolderBox.TextChanged += (_, _) => UpdateFolderPrompt();
        UpdateFolderPrompt();
        InheritHint.Text = string.IsNullOrEmpty(group?.Command) ? "plain shell" : group.Command;
        ModeBox.SelectedIndex = (int)(tab.Command switch
        {
            null => CommandMode.Inherit,
            "" => CommandMode.Shell,
            _ => CommandMode.Custom,
        });
        CustomBox.Text = tab.Command ?? "";
        _sessionId = tab.ClaudeSessionId;
        SessionBox.Text = _sessionId.ToString();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void UpdateFolderPrompt() => FolderPrompt.Visibility = FolderBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var mode = (CommandMode)ModeBox.SelectedIndex;
        CustomBox.Visibility = mode == CommandMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        InheritHint.Visibility = mode == CommandMode.Inherit ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (Desktop.ChooseFolder(FolderBox.Text, owner: this) is { } folder) FolderBox.Text = folder;
    }

    private void Fresh_Click(object sender, RoutedEventArgs e)
    {
        _sessionId = Guid.NewGuid();
        SessionBox.Text = _sessionId.ToString();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_store.Tab(_tabId) is { } tab)
        {
            if (NameBox.Text.Trim().Length > 0) tab.Name = NameBox.Text;
            tab.Directory = FolderBox.Text.Length == 0 ? null : FolderBox.Text;
            tab.Command = (CommandMode)ModeBox.SelectedIndex switch
            {
                CommandMode.Inherit => null,
                CommandMode.Shell => "",
                _ => CustomBox.Text.Length == 0 ? null : CustomBox.Text,
            };
            tab.ClaudeSessionId = _sessionId;
            _store.Touch();
        }
        Close();
    }
}
