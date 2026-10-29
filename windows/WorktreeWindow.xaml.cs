using System.Windows;
using System.Windows.Controls;

namespace Omux;

/// <summary>New Session in Worktree: a name, a branch made from it, and the branch to start from.</summary>
public partial class WorktreeWindow : Window
{
    private readonly Store _store = Store.Shared;
    private readonly Guid _groupId;
    private string? _repo;
    private bool _branchEdited;
    private bool _settingBranch;
    private bool _working;

    public WorktreeWindow(Guid groupId)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Desktop.StyleTitleBar(this, Theme.Current);
        _groupId = groupId;
        var group = _store.Group(groupId);
        UseWorktrees.Content = $"Use worktrees for new sessions in {group?.Name ?? "this project"}";
        UseWorktrees.IsChecked = group?.UseWorktrees == true;
        NameBox.Text = $"Session {(group?.Tabs.Count ?? 0) + 1}";
        BaseBox.SelectionChanged += (_, _) => UpdateCanCreate();
        Loaded += async (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
            await Load(group?.Directory);
        };
        UpdateCanCreate();
    }

    private async Task Load(string? folder)
    {
        if (folder is null)
        {
            ShowNotRepo("This project has no folder. Set one in Edit Project first.");
            return;
        }
        var dir = Paths.Expand(folder);
        var (repo, branches, current) = await Task.Run(() =>
        {
            var top = Git.TopLevel(dir);
            return top is null ? ((string?)null, new List<string>(), (string?)null) : (top, Git.Branches(top), Git.CurrentBranch(top));
        });
        _repo = repo;
        if (repo is null)
        {
            ShowNotRepo($"{folder} isn't a Git repository.");
            return;
        }
        BaseBox.ItemsSource = branches;
        BaseBox.SelectedItem = current ?? branches.FirstOrDefault();
        UpdateFolder();
        UpdateCanCreate();
    }

    private void ShowNotRepo(string message)
    {
        NotRepo.Text = message;
        NotRepo.Visibility = Visibility.Visible;
        Fields.Visibility = Visibility.Collapsed;
        UpdateCanCreate();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_branchEdited)
        {
            _settingBranch = true;
            BranchBox.Text = Git.Slug(NameBox.Text);
            _settingBranch = false;
        }
        UpdateCanCreate();
    }

    private void BranchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingBranch) _branchEdited = true;
        UpdateFolder();
        UpdateCanCreate();
    }

    private void UpdateFolder() =>
        FolderText.Text = _repo is not null && BranchBox.Text.Length > 0 ? Paths.Abbreviate(Git.WorktreePath(_repo, BranchBox.Text)) : "";

    private void UpdateCanCreate()
    {
        var branch = BranchBox.Text;
        CreateButton.IsEnabled = _repo is not null && !_working && NameBox.Text.Trim().Length > 0
            && branch.Length > 0 && !branch.Contains(' ') && BaseBox.SelectedItem is string;
    }

    private void UseWorktrees_Click(object sender, RoutedEventArgs e)
    {
        if (_store.Group(_groupId) is not { } group) return;
        group.UseWorktrees = UseWorktrees.IsChecked == true;
        _store.Touch();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!CreateButton.IsEnabled || BaseBox.SelectedItem is not string @base) return;
        _working = true;
        Working.Visibility = Visibility.Visible;
        Error.Visibility = Visibility.Collapsed;
        UpdateCanCreate();
        var message = await _store.CreateWorktreeSession(_groupId, NameBox.Text.Trim(), BranchBox.Text, @base);
        _working = false;
        Working.Visibility = Visibility.Collapsed;
        UpdateCanCreate();
        if (message is null)
        {
            Close();
            return;
        }
        Error.Text = message;
        Error.Visibility = Visibility.Visible;
    }
}
