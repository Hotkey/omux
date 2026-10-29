using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Omux;

public enum TermEventKind { Bell, Title, Directory, Status, OpenPath, Notification }

/// <summary>
/// Status carries the root record's state, Kind and Text (its message) plus every other
/// record; Notification carries Title and Text (the body) from OSC 9 or OSC 777.
/// </summary>
public sealed record TermEvent(TermEventKind Kind, string? Text = null, StatusState? State = null,
    string? StatusKind = null, IReadOnlyList<StatusChild>? Children = null, string? Title = null);

/// <summary>
/// One WebView2 that renders every session's terminal (xterm.js, one instance per session).
/// Sessions stay alive while hidden, so switching tabs never recreates a terminal.
/// </summary>
public sealed class TerminalSurface : ContentControl
{
    public static readonly TerminalSurface Shared = new();

    /// <summary>Keystrokes typed into a terminal.</summary>
    public event Action<Guid, string>? Input;
    public event Action<Guid, int, int>? Resized;
    public event Action<Guid, TermEvent>? TermEvent;
    /// <summary>An app shortcut pressed while the terminal had focus.</summary>
    public event Action<string>? Shortcut;

    private readonly WebView2 _web = new();
    private readonly List<string> _queue = [];
    private readonly Dictionary<Guid, StringBuilder> _pendingOutput = [];
    private readonly DispatcherTimer _flushTimer;
    private bool _ready;

    private TerminalSurface()
    {
        Content = _web;
        Focusable = false;
        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Render, (_, _) => FlushOutput(), Dispatcher);
    }

    public async Task InitializeAsync(CoreWebView2Environment environment)
    {
        await _web.EnsureCoreWebView2Async(environment);
        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.PermissionRequested += (_, e) =>
        {
            if (e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead) e.State = CoreWebView2PermissionState.Allow;
        };
        core.SetVirtualHostNameToFolderMapping("omux.terminal", Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Deny);
        core.WebMessageReceived += OnMessage;
        core.Navigate("https://omux.terminal/terminal.html");
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        string Str(string name) => Text(m, name) ?? "";
        Guid Id() => Guid.TryParse(Str("id"), out var id) ? id : Guid.Empty;

        switch (Str("t"))
        {
            case "ready":
                _ready = true;
                foreach (var message in _queue) _web.CoreWebView2.PostWebMessageAsJson(message);
                _queue.Clear();
                break;
            case "in": Input?.Invoke(Id(), Str("d")); break;
            case "resize": Resized?.Invoke(Id(), m.GetProperty("cols").GetInt32(), m.GetProperty("rows").GetInt32()); break;
            case "title": TermEvent?.Invoke(Id(), new(TermEventKind.Title, Str("title"))); break;
            case "bell": TermEvent?.Invoke(Id(), new(TermEventKind.Bell)); break;
            case "cwd": TermEvent?.Invoke(Id(), new(TermEventKind.Directory, Str("path"))); break;
            case "openPath": TermEvent?.Invoke(Id(), new(TermEventKind.OpenPath, Str("path"))); break;
            case "openUrl": Desktop.Open(Str("url")); break;
            case "shortcut": Shortcut?.Invoke(Str("name")); break;
            case "notify": TermEvent?.Invoke(Id(), new(TermEventKind.Notification, Str("body"), Title: Text(m, "title"))); break;
            case "status":
                var children = m.TryGetProperty("children", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(c => new StatusChild(Text(c, "id") ?? "", State(c), Text(c, "kind"), Text(c, "message"), Text(c, "title"))).ToList()
                    : [];
                TermEvent?.Invoke(Id(), new(TermEventKind.Status, Text(m, "message"), State(m), Text(m, "kind"), children));
                break;
        }
    }

    /// <summary>A non-empty string property, else null.</summary>
    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static StatusState? State(JsonElement element) =>
        Enum.TryParse<StatusState>(Text(element, "state"), ignoreCase: true, out var parsed) ? parsed : null;

    private void Post(object message)
    {
        var json = JsonSerializer.Serialize(message);
        if (_ready) _web.CoreWebView2.PostWebMessageAsJson(json);
        else _queue.Add(json);
    }

    public void Create(Guid id) => Post(new { t = "create", id });

    public void Show(Guid id)
    {
        FlushOutput();
        Post(new { t = "show", id });
    }

    public void Destroy(Guid id)
    {
        lock (_pendingOutput) _pendingOutput.Remove(id);
        Post(new { t = "destroy", id });
    }

    public void ProcessExited(Guid id) => Post(new { t = "exited", id });

    public void FocusTerminal()
    {
        _web.Focus();
        Post(new { t = "focus" });
    }

    public void ApplyAppearance(Theme theme) => Post(new
    {
        t = "appearance",
        theme = theme.ToXterm(),
        chrome = Theme.Css(theme.Chrome),
        fontFamily = Fonts.TerminalFamily,
        fontSize = Fonts.TerminalSize,
    });

    /// <summary>Opens the find bar over the visible terminal.</summary>
    public void Find()
    {
        _web.Focus();
        Post(new { t = "find" });
    }

    public void SetShortcuts(IEnumerable<object> list) => Post(new { t = "shortcuts", list });

    /// <summary>Types text into a session's prompt as a paste; submit presses Enter after it.</summary>
    public void Paste(Guid id, string text, bool submit) => Post(new { t = "paste", id, text, submit });

    /// <summary>Output may arrive on any thread; it is batched and written on the UI thread.</summary>
    public void Write(Guid id, string data)
    {
        lock (_pendingOutput)
        {
            if (!_pendingOutput.TryGetValue(id, out var buffer)) _pendingOutput[id] = buffer = new StringBuilder();
            buffer.Append(data);
        }
        Dispatcher.BeginInvoke(() => { if (!_flushTimer.IsEnabled) _flushTimer.Start(); });
    }

    private void FlushOutput()
    {
        _flushTimer.Stop();
        List<(Guid, string)> chunks;
        lock (_pendingOutput)
        {
            chunks = _pendingOutput.Select(p => (p.Key, p.Value.ToString())).ToList();
            _pendingOutput.Clear();
        }
        foreach (var (id, data) in chunks) Post(new { t = "out", id, d = data });
    }

    /// <summary>The session's scrollback as plain text, soft-wrapped rows joined.</summary>
    public async Task<string> BufferTextAsync(Guid id)
    {
        if (!_ready) return "";
        var json = await _web.CoreWebView2.ExecuteScriptAsync($"window.omuxBufferText('{id}')");
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }
}
