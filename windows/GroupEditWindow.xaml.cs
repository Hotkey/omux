using System.Windows;
using System.Windows.Controls;

namespace Omux;

public partial class GroupEditWindow : Window
{
    private readonly Store _store = Store.Shared;
    private readonly Guid _groupId;

    public GroupEditWindow(Guid groupId)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Desktop.StyleTitleBar(this, Theme.Current);
        _groupId = groupId;
        if (_store.Group(groupId) is not { } group) return;
        NameBox.Text = group.Name;
        FolderBox.Text = group.Directory ?? "";
        CommandBox.Text = group.Command ?? "";
        FolderBox.TextChanged += (_, _) => UpdatePrompts();
        CommandBox.TextChanged += (_, _) => UpdatePrompts();
        UpdatePrompts();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void UpdatePrompts()
    {
        FolderPrompt.Visibility = FolderBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CommandPrompt.Visibility = CommandBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (Desktop.ChooseFolder(FolderBox.Text, owner: this) is { } folder) FolderBox.Text = folder;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_store.Group(_groupId) is { } group)
        {
            if (NameBox.Text.Trim().Length > 0) group.Name = NameBox.Text;
            group.Directory = FolderBox.Text.Length == 0 ? null : FolderBox.Text;
            group.Command = CommandBox.Text;
            _store.Touch();
        }
        Close();
    }
}
