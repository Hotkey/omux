using System.IO;

namespace Omux;

/// <summary>Worktree sessions: their own branch and folder, next to the repo.</summary>
public sealed partial class Store
{
    /// <summary>Creates the worktree off the UI thread, then adds a session that runs in it. Returns an error message, or null.</summary>
    public async Task<string?> CreateWorktreeSession(Guid groupId, string name, string branch, string @base)
    {
        if (Group(groupId)?.Directory is not { } folder) return "This project has no folder.";
        var dir = Paths.Expand(folder);
        var (repo, result) = await Task.Run(() =>
        {
            var top = Git.TopLevel(dir);
            return (top, top is null ? null : Git.AddWorktree(top, branch, @base));
        });
        if (repo is null || result is null) return $"{dir} isn't a Git repository.";
        if (!result.Ok) return result.Message;
        AddSession(new TabItem(name, Git.WorktreePath(repo, branch), worktree: new Worktree(branch, @base, repo)), groupId);
        return null;
    }

    /// <summary>Ctrl+Shift+W: a worktree session asks whether to keep its worktree; others close at once.</summary>
    public void RequestClose(Guid id)
    {
        if (Tab(id) is not { Worktree: { } worktree } tab || FolderOf(tab) is not { } path || !Directory.Exists(path))
        {
            CloseTab(id);
            return;
        }
        var dirty = Git.DirtyCount(path);
        var ahead = Git.Ahead(worktree);
        string detail;
        if (tab.Runtime.PullRequest is { State: PullRequestState.Merged } pr)
        {
            // A squash or rebase merge leaves the branch's commits "not in" the base.
            detail = $"Its pull request #{pr.Number} was merged." + (dirty > 0 ? $" The worktree has {Files(dirty, "uncommitted file")}." : "");
        }
        else
        {
            var facts = new List<string>();
            if (dirty > 0) facts.Add(Files(dirty, "uncommitted file"));
            if (ahead > 0) facts.Add($"{Files(ahead, "commit")} not in {worktree.Base}");
            detail = facts.Any()
                ? $"Its worktree on “{worktree.Branch}” has {string.Join(" and ", facts)}."
                : $"Its worktree on “{worktree.Branch}” has nothing that isn't already in {worktree.Base}.";
        }
        switch (Alert.Show($"Close “{tab.Name}”?", detail, "Keep Worktree", "Delete Worktree and Branch", "Cancel"))
        {
            case 0:
                CloseTab(id);
                break;
            case 1:
                CloseTab(id);
                DeleteWorktree(worktree, path);
                break;
        }
    }

    /// <summary>Offered once the session's pull request is merged: closes the session and deletes its worktree and local branch.</summary>
    public void CleanUpMergedWorktree(Guid id)
    {
        if (Tab(id) is not { Worktree: { } worktree } tab || FolderOf(tab) is not { } path || !Directory.Exists(path)) return;
        var dirty = Git.DirtyCount(path);
        var number = tab.Runtime.PullRequest is { } pr ? $" in #{pr.Number}" : "";
        var detail = $"“{worktree.Branch}” was merged{number}. This closes the session and deletes its worktree and local branch."
            + (dirty > 0 ? $" {Files(dirty, "uncommitted file")} will be lost." : "");
        if (Alert.Show($"Delete the worktree for “{tab.Name}”?", detail, "Delete Worktree and Branch", "Cancel") != 0) return;
        CloseTab(id);
        DeleteWorktree(worktree, path);
    }

    private static async void DeleteWorktree(Worktree worktree, string path)
    {
        // The session's shell may still be letting go of the folder.
        await Task.Delay(500);
        var result = await Task.Run(() => Git.RemoveWorktree(worktree, path));
        if (!result.Ok) Alert.Show("Couldn't delete the worktree", result.Message);
    }

    /// <summary>Commits, pushes the branch and opens GitHub's pull request page.</summary>
    public async void CreatePullRequest(Guid id)
    {
        if (Tab(id) is not { Worktree: { } worktree } tab || FolderOf(tab) is not { } path || !CommitIfNeeded(path, worktree.Branch, tab.Name)) return;
        tab.Runtime.GitActivity = $"Pushing {worktree.Branch}…";
        var (result, url) = await Task.Run(() => Git.PushForPullRequest(worktree, path));
        tab.Runtime.GitActivity = null;
        if (!result.Ok) Alert.Show($"Couldn't push “{worktree.Branch}”", result.Message);
        else if (url is not null) Desktop.Open(url);
        else Alert.Show($"Pushed “{worktree.Branch}”", "The remote isn't on GitHub, so open the pull request on your host.");
        PullRequestMonitor.Shared.Poll();
    }

    /// <summary>Merges the session's branch into its base branch in the main checkout.</summary>
    public async void MergeWorktree(Guid id)
    {
        if (Tab(id) is not { Worktree: { } worktree } tab || FolderOf(tab) is not { } path || !CommitIfNeeded(path, worktree.Branch, tab.Name)) return;
        tab.Runtime.GitActivity = $"Merging into {worktree.Base}…";
        var result = await Task.Run(() => Git.Merge(worktree));
        tab.Runtime.GitActivity = null;
        if (result.Ok) Alert.Show($"Merged “{worktree.Branch}” into “{worktree.Base}”", result.Out.Trim());
        else Alert.Show($"Couldn't merge “{worktree.Branch}”", result.Message);
    }

    /// <summary>Offers to commit uncommitted work first. Returns false when the user cancels or the commit fails.</summary>
    private static bool CommitIfNeeded(string path, string branch, string suggestion)
    {
        var dirty = Git.DirtyCount(path);
        if (dirty == 0) return true;
        var (choice, text) = Alert.Ask($"Commit {Files(dirty, "changed file")} first?",
            $"They'll be committed to “{branch}”. Uncommitted changes aren't part of a merge or pull request.", suggestion, "Commit", "Cancel");
        if (choice != 0) return false;
        var message = text.Trim();
        var result = Git.CommitAll(path, message.Length > 0 ? message : suggestion);
        if (!result.Ok) Alert.Show("Couldn't commit", result.Message);
        return result.Ok;
    }

    /// <summary>"1 commit", "3 commits".</summary>
    private static string Files(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
