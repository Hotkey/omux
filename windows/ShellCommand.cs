using System.Collections;
using System.IO;

namespace Omux;

/// <summary>The shell a session runs in, and how a startup command is handed to it.</summary>
public sealed record ShellCommand(string Executable, ShellCommand.Kind ShellKind)
{
    public enum Kind { PowerShell, Cmd, Other }

    /// <summary>
    /// PowerShell and cmd can run the startup command and stay open afterwards
    /// (-NoExit -Command / /K), which is more reliable than typing ahead into a shell
    /// that is still loading. Other shells get the command typed in, as on macOS.
    /// </summary>
    public bool RunsStartupCommand => ShellKind != Kind.Other;

    public string CommandLine(string? startup)
    {
        var exe = $"\"{Executable}\"";
        var hasStartup = !string.IsNullOrWhiteSpace(startup);
        return ShellKind switch
        {
            Kind.PowerShell => hasStartup ? $"{exe} -NoLogo -NoExit -Command {startup}" : $"{exe} -NoLogo",
            Kind.Cmd => hasStartup ? $"{exe} /K {startup}" : exe,
            _ => Path.GetFileNameWithoutExtension(Executable).Equals("bash", StringComparison.OrdinalIgnoreCase) ? $"{exe} -l" : exe,
        };
    }

    /// <summary>The shell from Settings; empty picks PowerShell 7, else Windows PowerShell.</summary>
    public static ShellCommand Resolve()
    {
        var chosen = Prefs.Current.Shell.Trim().Trim('"');
        var exe = chosen.Length > 0
            ? FindExecutable(chosen) ?? chosen
            : FindExecutable("pwsh.exe")
              ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        var kind = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant() switch
        {
            "pwsh" or "powershell" => Kind.PowerShell,
            "cmd" => Kind.Cmd,
            _ => Kind.Other,
        };
        return new ShellCommand(exe, kind);
    }

    private static string? FindExecutable(string name)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        if (!Path.HasExtension(name)) name += ".exe";
        var dirs = (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "PowerShell", "7"));
        foreach (var dir in dirs)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// omux's environment, minus anything that leaks in from whoever launched it
    /// (e.g. a Claude session's CLAUDECODE / CLAUDE_CODE_* markers).
    /// </summary>
    public static Dictionary<string, string> Environment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (key.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)) continue;
            env[key] = entry.Value as string ?? "";
        }
        env["TERM"] = "xterm-256color";
        env["COLORTERM"] = "truecolor";
        env["TERM_PROGRAM"] = "omux";
        return env;
    }

    /// <summary>
    /// Windows consoles title themselves with the shell's own path ("C:\…\pwsh.exe");
    /// that says nothing, so it is not shown as the session's status.
    /// </summary>
    public static bool IsShellTitle(string? title) =>
        title is null || title.TrimEnd().EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
}
