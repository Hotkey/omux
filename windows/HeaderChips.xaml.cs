using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Omux;

/// <summary>The session header's chips: subagents, the pull request and the worktree's branch.</summary>
public partial class HeaderChips : UserControl
{
    private readonly Store _store = Store.Shared;
    private readonly DispatcherTimer _clock;
    private TabItem? _tab;

    public HeaderChips()
    {
        InitializeComponent();
        // The agent list's clocks tick while it is open.
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => AgentRows.Items.Refresh();
        _store.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Store.SelectedTabId)) Follow(); };
        Changes.Shared.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(Changes.Files) or nameof(Changes.TabId)) Update(); };
        Follow();
    }

    private void Follow()
    {
        if (_tab is not null) _tab.Runtime.PropertyChanged -= OnRuntimeChanged;
        _tab = _store.SelectedTab;
        if (_tab is not null) _tab.Runtime.PropertyChanged += OnRuntimeChanged;
        AgentPopup.IsOpen = false;
        PullPopup.IsOpen = false;
        Update();
    }

    private void OnRuntimeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabRuntime.Agents) or nameof(TabRuntime.PullRequest)) Update();
    }

    private void Update()
    {
        var agents = _tab?.Runtime.Agents ?? [];
        AgentPill.Visibility = agents.Any() ? Visibility.Visible : Visibility.Collapsed;
        AgentDots.ItemsSource = agents.Take(8).ToList();
        var running = agents.Count(a => !a.Finished);
        AgentText.Text = running == 0 ? "agents done" : $"{running} agent{(running == 1 ? "" : "s")}";
        AgentCounts.Text = $"{running} running · {agents.Count - running} done";
        AgentRows.ItemsSource = agents;
        if (!agents.Any()) AgentPopup.IsOpen = false;

        var pr = _tab?.Runtime.PullRequest;
        PullChip.Visibility = _tab?.Worktree is not null && pr is not null ? Visibility.Visible : Visibility.Collapsed;
        if (pr is not null)
        {
            PullNumber.Text = $"#{pr.Number}";
            PullDot.Fill = pr.Brush;
            PullSummary.Text = pr.Summary;
            PullChip.ToolTip = $"Pull request #{pr.Number}: {pr.Title}";
            PullTitle.Text = pr.Title;
            PullFacts.Text = pr.Facts;
            Checks.ItemsSource = pr.Checks;
            NoChecks.Visibility = pr.Checks.Any() ? Visibility.Collapsed : Visibility.Visible;
            DeleteMerged.Visibility = pr.IsMerged ? Visibility.Visible : Visibility.Collapsed;
        }
        else PullPopup.IsOpen = false;

        var worktree = _tab?.Worktree;
        BranchChip.Visibility = worktree is null ? Visibility.Collapsed : Visibility.Visible;
        if (worktree is not null)
        {
            BranchName.Text = worktree.Branch;
            BranchChip.ToolTip = $"Changes on {worktree.Branch} since {worktree.Base} (Ctrl+Shift+2)";
            var counted = Changes.Shared.TabId == _tab?.Id && Changes.Shared.Files.Any();
            var (added, removed) = Changes.Shared.Totals;
            BranchAdded.Text = counted ? $"+{added}" : "";
            BranchRemoved.Text = counted ? $"−{removed}" : "";
        }
    }

    private void AgentPill_Click(object sender, RoutedEventArgs e)
    {
        AgentPopup.IsOpen = !AgentPopup.IsOpen;
        if (AgentPopup.IsOpen) _clock.Start();
    }

    private void AgentPopup_Closed(object? sender, EventArgs e) => _clock.Stop();

    private void AgentRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SubAgent agent || _tab is null) return;
        AgentPopup.IsOpen = false;
        _store.OpenAgentTranscript(agent, _tab.Id);
    }

    private void PullChip_Click(object sender, RoutedEventArgs e) => PullPopup.IsOpen = !PullPopup.IsOpen;

    private void Check_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PullRequestCheck { Url: { } url }) Desktop.Open(url);
    }

    private void OpenPull_Click(object sender, RoutedEventArgs e)
    {
        PullPopup.IsOpen = false;
        if (_tab?.Runtime.PullRequest is { } pr) Desktop.Open(pr.Url);
    }

    private void DeleteMerged_Click(object sender, RoutedEventArgs e)
    {
        PullPopup.IsOpen = false;
        if (_tab is not null) _store.CleanUpMergedWorktree(_tab.Id);
    }

    private void BranchChip_Click(object sender, RoutedEventArgs e) => Store.ShowPanel(PanelTab.Changes);
}
