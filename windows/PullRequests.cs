using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Omux;

public enum PullRequestState { Open, Closed, Merged }
public enum PullRequestReview { Approved, ChangesRequested, Required }
public enum CheckResult { Pending, Passed, Failed, Skipped }

public sealed record PullRequestCheck(string Name, CheckResult Result, string? Url)
{
    public Brush Brush => new SolidColorBrush(Result switch
    {
        CheckResult.Passed => TabColors.Hex(0x30D158),
        CheckResult.Failed => TabColors.Hex(0xFF453A),
        CheckResult.Pending => TabColors.Hex(0x0A84FF),
        _ => TabColors.Hex(0x8E8E93),
    });

    public string Label => Result switch
    {
        CheckResult.Passed => "passed",
        CheckResult.Failed => "failed",
        CheckResult.Pending => "running",
        _ => "skipped",
    };
}

/// <summary>The pull request for a worktree session's branch, as `gh pr view` reports it.</summary>
public sealed record PullRequest(int Number, string Title, string Url, PullRequestState State, bool IsDraft,
    PullRequestReview? Review, IReadOnlyList<PullRequestCheck> Checks)
{
    /// <summary>The checks taken together: any failure wins, then anything still running.</summary>
    public CheckResult? ChecksResult
    {
        get
        {
            foreach (var result in new[] { CheckResult.Failed, CheckResult.Pending, CheckResult.Passed })
                if (Checks.Any(c => c.Result == result)) return result;
            return null;
        }
    }

    /// <summary>The most pressing fact about it, for tight spaces.</summary>
    public string Summary
    {
        get
        {
            if (State == PullRequestState.Merged) return "Merged";
            if (State == PullRequestState.Closed) return "Closed";
            if (ChecksResult == CheckResult.Failed) return "Checks failing";
            if (Review == PullRequestReview.ChangesRequested) return "Changes requested";
            if (ChecksResult == CheckResult.Pending) return "Checks running";
            if (IsDraft) return "Draft";
            if (Review == PullRequestReview.Approved) return "Approved";
            if (ChecksResult == CheckResult.Passed) return "Checks passed";
            return "Open";
        }
    }

    public Brush Brush => new SolidColorBrush(TabColors.Hex(Summary switch
    {
        "Merged" => 0xBF5AF2,
        "Closed" or "Draft" => 0x8E8E93,
        "Checks failing" => 0xFF453A,
        "Changes requested" => 0xFF9F0A,
        "Checks running" => 0x0A84FF,
        _ => 0x30D158,
    }));

    public bool IsMerged => State == PullRequestState.Merged;
    public bool IsOpen => State == PullRequestState.Open;

    /// <summary>"#42 · Open · Approved".</summary>
    public string Facts
    {
        get
        {
            var facts = new List<string> { $"#{Number}", IsDraft && IsOpen ? "Draft" : State.ToString() };
            if (Review is { } review)
                facts.Add(review switch
                {
                    PullRequestReview.Approved => "Approved",
                    PullRequestReview.ChangesRequested => "Changes requested",
                    _ => "Review required",
                });
            return string.Join(" · ", facts);
        }
    }

    /// <summary>Changes worth pulling the user back to the session for.</summary>
    public bool NeedsAttention(PullRequest old) =>
        ChecksResult == CheckResult.Failed && old.ChecksResult != CheckResult.Failed
        || Review != old.Review && Review is PullRequestReview.ChangesRequested or PullRequestReview.Approved
        || State == PullRequestState.Merged && old.State != PullRequestState.Merged;

    public bool Equals(PullRequest? other) =>
        other is not null && Number == other.Number && Title == other.Title && Url == other.Url && State == other.State
        && IsDraft == other.IsDraft && Review == other.Review && Checks.SequenceEqual(other.Checks);

    public override int GetHashCode() => HashCode.Combine(Number, State, Review, Checks.Count);
}

/// <summary>
/// Polls GitHub, through gh, for the pull requests of worktree sessions' branches, about once a
/// minute and whenever omux comes back to the front. Does nothing when gh isn't installed.
/// </summary>
public sealed class PullRequestMonitor
{
    public static PullRequestMonitor Shared { get; } = new();

    private readonly DispatcherTimer _timer;
    private bool _polling;

    private abstract record Lookup;
    private sealed record Found(PullRequest Request) : Lookup;
    private sealed record None : Lookup;
    /// <summary>Offline or rate limited: keep what we knew.</summary>
    private sealed record Unknown : Lookup;

    private PullRequestMonitor()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start(Window window)
    {
        if (Git.GhPath is null) return;
        _timer.Start();
        // Coming back from the browser is when a new pull request most likely exists.
        window.Activated += (_, _) => Poll();
        Poll();
    }

    public async void Poll()
    {
        if (_polling || Git.GhPath is null) return;
        var store = Store.Shared;
        var sessions = store.AllTabs
            .Where(e => e.Tab.Worktree is not null && store.FolderOf(e.Tab) is { } dir && Directory.Exists(dir))
            .Select(e => (e.Tab, Dir: store.FolderOf(e.Tab)!))
            .ToList();
        if (!sessions.Any()) return;
        _polling = true;
        var found = await Task.Run(() => sessions.Select(s => (s.Tab, Lookup: Find(s.Dir))).ToList());
        _polling = false;
        foreach (var (tab, lookup) in found)
        {
            switch (lookup)
            {
                case Found { Request: var pr }:
                    if (tab.Runtime.PullRequest is { } old && pr.NeedsAttention(old)) store.RequestAttention(tab.Id);
                    if (!pr.Equals(tab.Runtime.PullRequest)) tab.Runtime.PullRequest = pr;
                    break;
                case None:
                    tab.Runtime.PullRequest = null;
                    break;
            }
        }
    }

    private const string Fields = "number,title,url,state,isDraft,reviewDecision,statusCheckRollup";

    /// <summary>
    /// Asks about the branch checked out in dir rather than naming it, so gh can follow the
    /// branch's upstream to a pull request opened from a fork.
    /// </summary>
    private static Lookup Find(string dir)
    {
        if (Git.Gh(["pr", "view", "--json", Fields], dir) is not { } result) return new None();
        if (!result.Ok) return result.Err.Contains("no pull requests found", StringComparison.OrdinalIgnoreCase) ? new None() : new Unknown();
        try
        {
            using var doc = JsonDocument.Parse(result.Out);
            var raw = doc.RootElement;
            if (WebUrl(Str(raw, "url")) is not { } url || !Enum.TryParse<PullRequestState>(Str(raw, "state"), ignoreCase: true, out var state)) return new Unknown();
            PullRequestReview? review = Str(raw, "reviewDecision") switch
            {
                "APPROVED" => PullRequestReview.Approved,
                "CHANGES_REQUESTED" => PullRequestReview.ChangesRequested,
                "REVIEW_REQUIRED" => PullRequestReview.Required,
                _ => null,
            };
            var checks = raw.TryGetProperty("statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Array
                ? rollup.EnumerateArray().Select(Check).ToList()
                : [];
            return new Found(new PullRequest(raw.GetProperty("number").GetInt32(), Str(raw, "title") ?? "", url, state,
                raw.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True, review, checks));
        }
        catch { return new Unknown(); }
    }

    /// <summary>Checks come as CheckRuns (status and conclusion) or as commit StatusContexts (state).</summary>
    private static PullRequestCheck Check(JsonElement check)
    {
        CheckResult result;
        if (Str(check, "state") is { } state)
            result = state switch { "SUCCESS" => CheckResult.Passed, "PENDING" or "EXPECTED" => CheckResult.Pending, _ => CheckResult.Failed };
        else if (Str(check, "status") != "COMPLETED")
            result = CheckResult.Pending;
        else
            result = Str(check, "conclusion") switch
            {
                "SUCCESS" => CheckResult.Passed,
                "SKIPPED" or "NEUTRAL" or "STALE" => CheckResult.Skipped,
                _ => CheckResult.Failed,
            };
        return new PullRequestCheck(Str(check, "name") ?? Str(check, "context") ?? "Check", result,
            WebUrl(Str(check, "detailsUrl") ?? Str(check, "targetUrl")));
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    /// <summary>Only web links: these open on click, and check links come from whoever reports the check.</summary>
    private static string? WebUrl(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri.AbsoluteUri : null;
}
