using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Omux;

/// <summary>Thin, synchronous wrappers around the git CLI. Call them off the UI thread.</summary>
public static partial class Git
{
    public sealed record Result(int Status, string Out, string Err)
    {
        public bool Ok => Status == 0;
        /// <summary>The most useful text to show when the command failed.</summary>
        public string Message => (Err.Length == 0 ? Out : Err).Trim();
    }

    /// <summary>
    /// Runs git. GIT_OPTIONAL_LOCKS=0 keeps status checks from fighting with whatever git the
    /// session itself is running.
    /// </summary>
    public static Result Run(IEnumerable<string> args, string dir) =>
        Exec("git", args, dir, new() { ["GIT_OPTIONAL_LOCKS"] = "0", ["GIT_TERMINAL_PROMPT"] = "0" });

    private static Result Exec(string file, IEnumerable<string> args, string dir, Dictionary<string, string> env)
    {
        var info = new ProcessStartInfo(file)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        // Not the CLAUDECODE markers omux may have inherited, like the sessions themselves.
        foreach (var (key, value) in ShellCommand.Environment()) info.Environment[key] = value;
        foreach (var (key, value) in env) info.Environment[key] = value;
        try
        {
            using var process = Process.Start(info)!;
            process.StandardInput.Close();
            // Read both pipes at once: a full pipe would otherwise block the child forever.
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return new Result(process.ExitCode, output.Result, error.Result);
        }
        catch (Exception e)
        {
            return new Result(-1, "", e.Message);
        }
    }

    /// <summary>Where gh is, looked up once. null when it isn't installed.</summary>
    public static readonly string? GhPath = FindGh();

    private static string? FindGh()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI"));
        foreach (var dir in dirs)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "gh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Runs the GitHub CLI, or returns null when it isn't installed.</summary>
    public static Result? Gh(IEnumerable<string> args, string dir) =>
        GhPath is { } gh ? Exec(gh, args, dir, new() { ["GH_PROMPT_DISABLED"] = "1", ["GIT_TERMINAL_PROMPT"] = "0", ["NO_COLOR"] = "1" }) : null;

    // MARK: Repository facts

    public static string? TopLevel(string dir)
    {
        var r = Run(["rev-parse", "--show-toplevel"], dir);
        return r.Ok ? Path.GetFullPath(r.Out.Trim()) : null;
    }

    public static string? CurrentBranch(string dir)
    {
        var r = Run(["symbolic-ref", "--quiet", "--short", "HEAD"], dir);
        return r.Ok ? r.Out.Trim() : null;
    }

    public static List<string> Branches(string dir) =>
        Run(["for-each-ref", "--format=%(refname:short)", "refs/heads"], dir).Out
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>The number of changed and untracked files.</summary>
    public static int DirtyCount(string dir) =>
        Run(["status", "--porcelain"], dir).Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Commits on branch that aren't in base.</summary>
    public static int Ahead(Worktree worktree) =>
        int.TryParse(Run(["rev-list", "--count", $"{worktree.Base}..{worktree.Branch}"], worktree.Repo).Out.Trim(), out var n) ? n : 0;

    [GeneratedRegex(@"[^\p{L}\p{N}_/.-]+")]
    private static partial Regex NotBranchCharacters();

    /// <summary>A branch name from free text: "Fix the login bug" → "fix-the-login-bug".</summary>
    public static string Slug(string text)
    {
        var words = NotBranchCharacters().Split(text.ToLowerInvariant()).Where(w => w.Length > 0);
        var slug = string.Join('-', words);
        return (slug.Length > 48 ? slug[..48] : slug).Trim('-', '.', '/');
    }

    // MARK: Worktrees

    /// <summary>Where a branch's worktree lives: next to the repo, in &lt;repo&gt;.worktrees\&lt;branch&gt;.</summary>
    public static string WorktreePath(string repo, string branch)
    {
        var full = Path.GetFullPath(repo).TrimEnd('\\', '/');
        return Path.Combine(Path.GetDirectoryName(full) ?? full, Path.GetFileName(full) + ".worktrees", branch.Replace('/', '-'));
    }

    /// <summary>Creates the worktree, reusing the branch when it already exists.</summary>
    public static Result AddWorktree(string repo, string branch, string @base)
    {
        var path = WorktreePath(repo, branch);
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); } catch { }
        return Branches(repo).Contains(branch)
            ? Run(["worktree", "add", path, branch], repo)
            : Run(["worktree", "add", "-b", branch, path, @base], repo);
    }

    public static Result RemoveWorktree(Worktree worktree, string path)
    {
        var removed = Run(["worktree", "remove", "--force", path], worktree.Repo);
        return removed.Ok ? Run(["branch", "-D", worktree.Branch], worktree.Repo) : removed;
    }

    public static Result CommitAll(string dir, string message)
    {
        var added = Run(["add", "-A"], dir);
        return added.Ok ? Run(["commit", "-m", message], dir) : added;
    }

    /// <summary>Merges the worktree's branch into its base in the main checkout.</summary>
    public static Result Merge(Worktree worktree)
    {
        if (CurrentBranch(worktree.Repo) is not { } current)
            return new Result(1, "", $"Couldn't read the branch checked out in {worktree.Repo}.");
        if (current != worktree.Base)
            return new Result(1, "", $"{worktree.Repo} has “{current}” checked out. Check out “{worktree.Base}” there first.");
        return Run(["merge", "--no-edit", worktree.Branch], worktree.Repo);
    }

    /// <summary>Pushes the branch and returns the page for opening a pull request, when the remote is on GitHub.</summary>
    public static (Result Result, string? Url) PushForPullRequest(Worktree worktree, string path)
    {
        // Pushing may need Git Credential Manager's sign-in window, so prompts stay allowed.
        var pushed = Exec("git", ["push", "-u", "origin", worktree.Branch], path, []);
        if (!pushed.Ok) return (pushed, null);
        var remote = Run(["remote", "get-url", "origin"], path).Out.Trim();
        return (pushed, CompareUrl(remote, worktree.Base, worktree.Branch));
    }

    public static string? CompareUrl(string remote, string @base, string branch)
    {
        var slug = remote;
        foreach (var prefix in new[] { "git@github.com:", "ssh://git@github.com/", "https://github.com/", "http://github.com/" })
        {
            if (slug.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) slug = slug[prefix.Length..];
        }
        if (slug == remote) return null;
        if (slug.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) slug = slug[..^4];
        static string Escape(string s) => string.Join('/', s.Split('/').Select(Uri.EscapeDataString));
        return $"https://github.com/{slug}/compare/{Escape(@base)}...{Escape(branch)}?expand=1";
    }

    // MARK: Diffs

    /// <summary>
    /// What a session's changes are measured against: the merge base with the worktree's base
    /// branch, or HEAD for an ordinary checkout. null when there is no commit yet.
    /// </summary>
    public static string? DiffBase(string dir, Worktree? worktree)
    {
        if (worktree is not null)
        {
            var r = Run(["merge-base", "HEAD", worktree.Base], dir);
            if (r.Ok) return r.Out.Trim();
        }
        return Run(["rev-parse", "--verify", "--quiet", "HEAD"], dir).Ok ? "HEAD" : null;
    }

    /// <summary>Changed and untracked files, compared with base (working tree included).</summary>
    public static List<ChangedFile> ChangedFiles(string dir, string? @base)
    {
        var files = new List<ChangedFile>();
        if (@base is not null)
        {
            var numstat = Run(["diff", "--numstat", "-z", "--no-renames", @base], dir).Out;
            var status = Run(["diff", "--name-status", "-z", "--no-renames", @base], dir).Out;
            var kinds = new Dictionary<string, ChangeStatus>();
            var parts = status.Split('\0');
            for (var i = 0; i + 1 < parts.Length; i += 2)
                kinds[parts[i + 1]] = parts[i].StartsWith('A') ? ChangeStatus.Added : parts[i].StartsWith('D') ? ChangeStatus.Deleted : ChangeStatus.Modified;
            foreach (var record in numstat.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = record.Split('\t', 3);
                if (fields.Length != 3) continue;
                files.Add(new ChangedFile(fields[2], Number(fields[0]), Number(fields[1]), kinds.GetValueOrDefault(fields[2], ChangeStatus.Modified)));
            }
        }
        var untracked = Run(["ls-files", "--others", "--exclude-standard", "-z"], dir).Out;
        foreach (var path in untracked.Split('\0', StringSplitOptions.RemoveEmptyEntries).Take(300))
        {
            var lines = TextLines(Path.Combine(dir, path))?.Count;
            files.Add(new ChangedFile(path, lines, lines is null ? null : 0, ChangeStatus.Untracked));
        }
        return files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int? Number(string text) => int.TryParse(text, out var n) ? n : null;

    /// <summary>The lines of a small text file, or null for binaries and big files.</summary>
    public static List<string>? TextLines(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length >= 1_000_000) return null;
            var data = File.ReadAllBytes(path);
            if (data.AsSpan(0, Math.Min(8000, data.Length)).Contains((byte)0)) return null;
            var lines = new UTF8Encoding(false, true).GetString(data).Replace("\r\n", "\n").Split('\n').ToList();
            if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }
        catch { return null; }
    }

    public static List<DiffLine> Diff(string dir, string? @base, ChangedFile file)
    {
        if (file.Status == ChangeStatus.Untracked || @base is null)
        {
            if (TextLines(Path.Combine(dir, file.Path)) is not { } lines)
                return [new DiffLine(0, DiffKind.Info, null, null, "Binary or large file")];
            return [new DiffLine(0, DiffKind.Hunk, null, null, "New file"),
                .. lines.Take(3000).Select((text, i) => new DiffLine(i + 1, DiffKind.Added, null, i + 1, text))];
        }
        return ParseDiff(Run(["diff", "--no-color", "--no-ext-diff", "--no-renames", @base, "--", file.Path], dir).Out);
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@(.*)$")]
    private static partial Regex HunkHeader();

    public static List<DiffLine> ParseDiff(string text)
    {
        var lines = new List<DiffLine>();
        int old = 0, @new = 0;
        var inHunk = false;
        foreach (var raw in text.Split('\n').Take(6000))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@") && HunkHeader().Match(line) is { Success: true } m)
            {
                old = int.Parse(m.Groups[1].Value);
                @new = int.Parse(m.Groups[2].Value);
                inHunk = true;
                lines.Add(new DiffLine(lines.Count, DiffKind.Hunk, null, null, line));
            }
            else if (line.StartsWith("Binary files"))
                lines.Add(new DiffLine(lines.Count, DiffKind.Info, null, null, "Binary file changed"));
            else if (!inHunk)
                continue;
            else if (line.StartsWith('+'))
                lines.Add(new DiffLine(lines.Count, DiffKind.Added, null, @new++, line[1..]));
            else if (line.StartsWith('-'))
                lines.Add(new DiffLine(lines.Count, DiffKind.Removed, old++, null, line[1..]));
            else if (line.StartsWith(' '))
                lines.Add(new DiffLine(lines.Count, DiffKind.Context, old++, @new++, line[1..]));
        }
        return lines;
    }
}

public enum ChangeStatus { Modified, Added, Deleted, Untracked }

/// <param name="Added">null for binary files.</param>
public sealed record ChangedFile(string Path, int? Added, int? Removed, ChangeStatus Status)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? "";

    public string StatusLetter => Status switch
    {
        ChangeStatus.Added => "A",
        ChangeStatus.Deleted => "D",
        ChangeStatus.Untracked => "U",
        _ => "M",
    };

    public System.Windows.Media.Brush StatusBrush => new System.Windows.Media.SolidColorBrush(TabColors.Hex(Status switch
    {
        ChangeStatus.Modified => 0xFF9F0A,
        ChangeStatus.Deleted => 0xFF453A,
        _ => 0x30D158,
    }));

    /// <summary>"+12" and "−3" in green and red; "binary" when git can't count lines.</summary>
    public string? AddedText => Added > 0 ? $"+{Added}" : null;
    public string? RemovedText => Removed > 0 ? $"−{Removed}" : null;
    public bool IsBinary => Added is null;
}

public enum DiffKind { Hunk, Context, Added, Removed, Info }

public sealed record DiffLine(int Index, DiffKind Kind, int? OldNumber, int? NewNumber, string Text)
{
    /// <summary>The line number a review note refers to.</summary>
    public int? Number => NewNumber ?? OldNumber;
}
