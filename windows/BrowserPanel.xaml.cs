using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Omux;

/// <summary>One session's embedded browser. The web view outlives tab switches, like terminals.</summary>
public sealed class BrowserSession : Observable
{
    public Guid TabId { get; }
    public WebView2 Web { get; } = new() { Visibility = Visibility.Collapsed };

    private string _address = "";
    private string? _currentUrl;
    private bool _isLoading;
    private bool _picking;
    private string? _error;
    private string? _notice;
    private readonly Task _ready;

    public BrowserSession(Guid tabId)
    {
        TabId = tabId;
        _ready = Initialize();
    }

    public string Address { get => _address; set => Set(ref _address, value); }
    public string? CurrentUrl { get => _currentUrl; private set => Set(ref _currentUrl, value); }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public bool Picking { get => _picking; private set => Set(ref _picking, value); }
    public string? Error { get => _error; private set => Set(ref _error, value); }
    /// <summary>A short confirmation after an element was sent to the prompt.</summary>
    public string? Notice { get => _notice; set => Set(ref _notice, value); }
    public bool CanGoBack => Web.CoreWebView2?.CanGoBack == true;
    public bool CanGoForward => Web.CoreWebView2?.CanGoForward == true;

    private async Task Initialize()
    {
        await Web.EnsureCoreWebView2Async(MainWindow.Instance.WebEnvironment);
        // Set once it exists: before that, WebView2 takes the process-wide default, which the
        // transparent file preview has already set.
        Web.DefaultBackgroundColor = System.Drawing.Color.White;
        var core = Web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, _) => { Error = null; Picking = false; IsLoading = true; };
        core.NavigationCompleted += (_, e) =>
        {
            IsLoading = false;
            if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) Error = Describe(e.WebErrorStatus);
            Raise(nameof(CanGoBack));
            Raise(nameof(CanGoForward));
        };
        core.SourceChanged += (_, _) =>
        {
            CurrentUrl = core.Source;
            Address = core.Source;
        };
        core.HistoryChanged += (_, _) =>
        {
            Raise(nameof(CanGoBack));
            Raise(nameof(CanGoForward));
        };
        // Links that open a new window stay in this tab.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };
        core.WebMessageReceived += (_, e) => Received(e.WebMessageAsJson);
    }

    private static string Describe(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.ConnectionReset or CoreWebView2WebErrorStatus.CannotConnect
            => "Could not connect to the server. Is it running?",
        CoreWebView2WebErrorStatus.HostNameNotResolved => "The server's name could not be found.",
        CoreWebView2WebErrorStatus.Timeout => "The server took too long to respond.",
        _ => status.ToString(),
    };

    /// <summary>Loads typed text; a bare "localhost:3000" or host gets http://.</summary>
    public async void Load(string text)
    {
        var raw = text.Trim();
        if (raw.Length == 0) return;
        if (!raw.Contains("://")) raw = "http://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            Error = "That isn't a valid address.";
            return;
        }
        Error = null;
        Address = raw;
        CurrentUrl = uri.AbsoluteUri;
        await _ready;
        Web.CoreWebView2.Navigate(uri.AbsoluteUri);
    }

    public void Reload()
    {
        if (Web.CoreWebView2 is null || Web.CoreWebView2.Source is null or "about:blank") Load(Address);
        else Web.CoreWebView2.Reload();
    }

    public void Stop() => Web.CoreWebView2?.Stop();
    public void GoBack() => Web.CoreWebView2?.GoBack();
    public void GoForward() => Web.CoreWebView2?.GoForward();

    public void Close() => Web.Dispose();

    // MARK: Element picker

    public async void TogglePick()
    {
        if (Web.CoreWebView2 is not { } core) return;
        if (Picking)
        {
            await core.ExecuteScriptAsync("window.__omuxPick && window.__omuxPick.stop()");
            Picking = false;
            return;
        }
        if (CurrentUrl is null) return;
        Notice = null;
        Picking = true;
        try
        {
            await core.ExecuteScriptAsync(PickScript);
            Web.Focus();
        }
        catch { Picking = false; }
    }

    private async void Received(string json)
    {
        Picking = false;
        using var doc = JsonDocument.Parse(json);
        var body = doc.RootElement;
        if (body.ValueKind != JsonValueKind.Object || body.TryGetProperty("cancel", out _)) return;
        string Text(string name, string fallback) => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
        double Number(string name) => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        var selector = Text("selector", "element");
        var page = Text("url", Address);

        var folder = Path.Combine(Path.GetTempPath(), "omux-picks", DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString());
        Directory.CreateDirectory(folder);
        var htmlPath = Path.Combine(folder, "element.html");
        File.WriteAllText(htmlPath, $"<!-- Picked in omux from {page} -->\n<!-- Selector: {selector} -->\n<!-- Computed styles:\n{Text("css", "")}\n-->\n{Text("html", "")}\n");

        var imagePath = await Screenshot(folder, Number("x"), Number("y"), Number("width"), Number("height"), Number("viewport"));
        var text = $"The {selector} element on {page}: HTML and computed styles in {htmlPath}";
        if (imagePath is not null) text += $", screenshot at {imagePath}";
        Store.Shared.SendText(TabId, text + ". ", submit: false);
        Notice = $"Added {selector} to the prompt";
    }

    /// <summary>Saves the element's part of the page as a PNG; CSS pixels are scaled to the capture's pixels.</summary>
    private async Task<string?> Screenshot(string folder, double x, double y, double width, double height, double viewport)
    {
        if (width < 1 || height < 1 || viewport < 1 || Web.CoreWebView2 is not { } core) return null;
        try
        {
            using var stream = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            stream.Position = 0;
            var page = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var scale = page.PixelWidth / viewport;
            var rect = new Int32Rect((int)(x * scale), (int)(y * scale), (int)(width * scale), (int)(height * scale));
            rect.X = Math.Clamp(rect.X, 0, page.PixelWidth - 1);
            rect.Y = Math.Clamp(rect.Y, 0, page.PixelHeight - 1);
            rect.Width = Math.Min(rect.Width, page.PixelWidth - rect.X);
            rect.Height = Math.Min(rect.Height, page.PixelHeight - rect.Y);
            if (rect.Width < 1 || rect.Height < 1) return null;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(page, rect)));
            var path = Path.Combine(folder, "element.png");
            await using var file = File.Create(path);
            encoder.Save(file);
            return path;
        }
        catch { return null; }
    }

    /// <summary>
    /// Highlights the element under the pointer; a click posts its selector, HTML, the styles
    /// that matter and its box. Escape cancels.
    /// </summary>
    private const string PickScript = """
        (function () {
          if (window.__omuxPick) window.__omuxPick.stop();
          const send = (message) => window.chrome.webview.postMessage(message);
          const box = document.createElement('div');
          const tag = document.createElement('div');
          box.style.cssText = 'position:fixed;pointer-events:none;z-index:2147483647;border:2px solid #22d3ee;background:rgba(34,211,238,0.12);border-radius:2px;display:none';
          tag.style.cssText = 'position:fixed;pointer-events:none;z-index:2147483647;background:#22d3ee;color:#0b0b0f;font:11px Consolas,monospace;padding:2px 6px;border-radius:3px;display:none';
          document.documentElement.append(box, tag);
          const name = (el) => {
            let s = el.tagName.toLowerCase();
            if (el.id) s += '#' + el.id;
            const cls = [...el.classList].filter((c) => !c.includes(':')).slice(0, 3);
            if (cls.length) s += '.' + cls.join('.');
            return s;
          };
          const path = (el) => {
            const parts = [];
            for (let n = el; n && n.nodeType === 1 && parts.length < 4; n = n.parentElement) {
              if ((n === document.body || n === document.documentElement) && parts.length) break;
              parts.unshift(name(n));
              if (n.id) break;
            }
            return parts.join(' > ');
          };
          let current = null;
          const move = (e) => {
            const el = e.target;
            if (!(el instanceof Element)) return;
            current = el;
            const r = el.getBoundingClientRect();
            Object.assign(box.style, { display: 'block', left: r.left + 'px', top: r.top + 'px', width: r.width + 'px', height: r.height + 'px' });
            tag.textContent = name(el) + '  ' + Math.round(r.width) + '×' + Math.round(r.height);
            Object.assign(tag.style, { display: 'block', left: Math.max(0, r.left) + 'px', top: Math.max(0, r.top - 20) + 'px' });
          };
          const PROPS = ['display', 'position', 'box-sizing', 'width', 'height', 'margin', 'padding', 'border', 'border-radius',
            'background-color', 'background-image', 'color', 'font-family', 'font-size', 'font-weight', 'line-height',
            'letter-spacing', 'text-align', 'gap', 'flex-direction', 'justify-content', 'align-items',
            'grid-template-columns', 'box-shadow', 'opacity', 'overflow'];
          const pick = (e) => {
            e.preventDefault();
            e.stopPropagation();
            const el = current || e.target;
            const cs = getComputedStyle(el);
            const css = PROPS.map((p) => p + ': ' + cs.getPropertyValue(p))
              .filter((l) => !/: (normal|none|auto|0px|static|visible|)$/.test(l)).join(';\n');
            const r = el.getBoundingClientRect();
            stop();
            // The highlight is gone before the host takes its screenshot.
            requestAnimationFrame(() => requestAnimationFrame(() => send({
              selector: path(el), html: el.outerHTML.slice(0, 30000), css, url: location.href,
              x: r.left, y: r.top, width: r.width, height: r.height, viewport: window.innerWidth })));
          };
          const block = (e) => { e.preventDefault(); e.stopPropagation(); };
          const key = (e) => {
            if (e.key !== 'Escape') return;
            stop();
            send({ cancel: true });
          };
          function stop() {
            document.removeEventListener('mousemove', move, true);
            document.removeEventListener('click', pick, true);
            document.removeEventListener('mousedown', block, true);
            document.removeEventListener('mouseup', block, true);
            document.removeEventListener('keydown', key, true);
            box.remove();
            tag.remove();
            window.__omuxPick = null;
          }
          document.addEventListener('mousemove', move, true);
          document.addEventListener('click', pick, true);
          document.addEventListener('mousedown', block, true);
          document.addEventListener('mouseup', block, true);
          document.addEventListener('keydown', key, true);
          window.__omuxPick = { stop };
        })();
        """;
}

/// <summary>The Browser tab: a web view per session that opens the local server the session prints.</summary>
public partial class BrowserPanel : UserControl
{
    private static readonly Dictionary<Guid, BrowserSession> Sessions = [];
    private static BrowserPanel? _instance;

    private readonly Store _store = Store.Shared;
    private BrowserSession? _session;
    private bool _active;

    public BrowserPanel()
    {
        InitializeComponent();
        _instance = this;
        _store.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Store.SelectedTabId) && _active) Show(); };
        _store.MentionedUrls.CollectionChanged += (_, _) =>
        {
            if (!_active) return;
            OpenMentionedServer();
            Update();
        };
        Address.TextChanged += (_, _) => AddressPrompt.Visibility = Address.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The session's web view is made the first time its Browser tab shows.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (active) Show();
        else if (_session is not null) _session.Web.Visibility = Visibility.Collapsed;
    }

    /// <summary>Stops and drops a closed session's browser.</summary>
    public static void Close(Guid tabId)
    {
        if (!Sessions.Remove(tabId, out var session)) return;
        _instance?.WebHost.Children.Remove(session.Web);
        if (_instance?._session == session) _instance._session = null;
        session.Close();
    }

    private void Show()
    {
        if (_session is not null) _session.PropertyChanged -= OnSessionChanged;
        _session = null;
        if (_store.SelectedTabId is { } id && MainWindow.Instance.WebEnvironment is not null)
        {
            if (!Sessions.TryGetValue(id, out var session))
            {
                Sessions[id] = session = new BrowserSession(id);
                WebHost.Children.Add(session.Web);
            }
            _session = session;
            session.PropertyChanged += OnSessionChanged;
            OpenMentionedServer();
        }
        Update();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e) => Update();

    /// <summary>The first time the tab shows, it opens the server the session printed last.</summary>
    private void OpenMentionedServer()
    {
        if (_session is { CurrentUrl: null, Address.Length: 0 } session && _store.MentionedUrls.LastOrDefault() is { } url) session.Load(url);
    }

    private void Update()
    {
        var session = _session;
        foreach (var web in WebHost.Children.OfType<WebView2>())
            web.Visibility = _active && web == session?.Web && session.CurrentUrl is not null && session.Error is null ? Visibility.Visible : Visibility.Collapsed;
        if (session is not null && !Address.IsKeyboardFocused && Address.Text != session.Address) Address.Text = session.Address;
        if (session is null) Address.Text = "";

        Back.IsEnabled = session?.CanGoBack == true;
        Forward.IsEnabled = session?.CanGoForward == true;
        Back.Opacity = Back.IsEnabled ? 1 : 0.35;
        Forward.Opacity = Forward.IsEnabled ? 1 : 0.35;
        ReloadButton.Content = session?.IsLoading == true ? "" : "";
        ReloadButton.ToolTip = session?.IsLoading == true ? "Stop" : "Reload";
        var hasPage = session?.CurrentUrl is not null;
        Pick.IsEnabled = External.IsEnabled = hasPage;
        Pick.Opacity = External.Opacity = hasPage ? 1 : 0.35;
        Pick.Foreground = (Brush)FindResource(session?.Picking == true ? "AccentBrush" : "SecondaryBrush");
        PickBanner.Visibility = session?.Picking == true ? Visibility.Visible : Visibility.Collapsed;
        Notice.Text = session?.Notice ?? "";
        NoticeBar.Visibility = session?.Notice is null ? Visibility.Collapsed : Visibility.Visible;

        Failure.Visibility = session?.Error is null ? Visibility.Collapsed : Visibility.Visible;
        FailureDetail.Text = session?.Error ?? "";
        Empty.Visibility = session is not null && (hasPage || session.Error is not null) ? Visibility.Collapsed : Visibility.Visible;
        var servers = _store.MentionedUrls.Reverse().Take(5).ToList();
        EmptyHint.Text = session is null
            ? "Select a session to browse its local server."
            : servers.Any() ? "Servers in this session's output:" : "Type an address above. Local servers this session prints, like localhost:3000, open here by themselves.";
        Servers.ItemsSource = session is null ? null : servers;
    }

    private void Address_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _session is null) return;
        e.Handled = true;
        _session.Load(Address.Text);
    }

    private void Server_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Hyperlink)?.DataContext is string url) _session?.Load(url);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _session?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => _session?.GoForward();

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        if (_session.IsLoading) _session.Stop();
        else _session.Reload();
    }

    private void Pick_Click(object sender, RoutedEventArgs e) => _session?.TogglePick();
    private void External_Click(object sender, RoutedEventArgs e) { if (_session?.CurrentUrl is { } url) Desktop.Open(url); }
    private void DismissNotice_Click(object sender, RoutedEventArgs e) { if (_session is not null) _session.Notice = null; }
}
