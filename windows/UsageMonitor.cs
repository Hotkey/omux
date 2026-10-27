using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;

namespace Omux;

/// <summary>
/// Reads Claude Code's OAuth token from its credentials file and polls the same usage
/// endpoint Claude Code's /usage command uses.
/// </summary>
public sealed class UsageMonitor : Observable
{
    public sealed class Limit : Observable
    {
        private bool _hovered;

        public required string Id { get; init; }
        public required string Label { get; init; }
        public required int Percent { get; init; }
        public DateTime? ResetsAt { get; init; }

        public bool Hovered { get => _hovered; set { if (Set(ref _hovered, value)) Raise(nameof(Text)); } }

        public string Text => Hovered ? ResetText : $"{Label} {Percent}%";

        public double Fraction => Math.Min(Percent, 100) / 100.0;

        public Brush Tint => Percent >= 90 ? new SolidColorBrush(TabColors.Hex(0xFF453A))
            : Percent >= 70 ? new SolidColorBrush(TabColors.Hex(0xFF9F0A))
            : (Brush)System.Windows.Application.Current.Resources["AccentBrush"];

        public string ResetText
        {
            get
            {
                if (ResetsAt is not { } resetsAt) return Label;
                var seconds = Math.Max(0, (int)(resetsAt - DateTime.UtcNow).TotalSeconds);
                int h = seconds / 3600, m = seconds % 3600 / 60;
                var when = seconds >= 86400 ? $"{seconds / 86400}d {h % 24}h" : h > 0 ? $"{h}h {m}m" : $"{m}m";
                return $"{Label} resets in {when}";
            }
        }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private string? _error;
    private readonly DispatcherTimer _timer = new();

    public ObservableCollection<Limit> Limits { get; } = [];
    public string? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(Placeholder)); } }
    public string? Placeholder => Limits.Any() ? null : Error ?? "Claude usage…";

    public UsageMonitor()
    {
        _timer.Tick += async (_, _) => await LoadAsync();
        Prefs.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Prefs.UsageInterval)) Schedule();
        };
    }

    /// <summary>Polls at the interval chosen in Settings; re-arms when it changes.</summary>
    public async void Start()
    {
        Schedule();
        await LoadAsync();
    }

    private void Schedule()
    {
        var interval = Prefs.Current.UsageInterval > 0 ? Prefs.Current.UsageInterval : 300;
        _timer.Interval = TimeSpan.FromSeconds(interval);
        _timer.Start();
    }

    private async Task LoadAsync()
    {
        var token = await Task.Run(ReadToken);
        if (token is null)
        {
            Error = "Not signed in to Claude Code";
            return;
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Error = "Usage unavailable";
                return;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Limits.Clear();
            foreach (var entry in doc.RootElement.GetProperty("limits").EnumerateArray())
            {
                var kind = entry.GetProperty("kind").GetString() ?? "";
                string? model = entry.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
                    && scope.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object
                    && m.TryGetProperty("display_name", out var name) ? name.GetString() : null;
                var label = kind switch { "session" => "5h", "weekly_all" => "Week", _ => model ?? kind };
                DateTime? resetsAt = entry.TryGetProperty("resets_at", out var resets) && resets.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(resets.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
                    ? date : null;
                Limits.Add(new Limit { Id = kind + model, Label = label, Percent = entry.GetProperty("percent").GetInt32(), ResetsAt = resetsAt });
            }
            Error = null;
            Raise(nameof(Placeholder));
        }
        catch
        {
            Error = "Usage unavailable";
        }
    }

    /// <summary>Claude Code on Windows keeps its login in ~/.claude/.credentials.json.</summary>
    private static string? ReadToken()
    {
        try
        {
            var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } custom ? custom : Path.Combine(Paths.Home, ".claude");
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, ".credentials.json")));
            return doc.RootElement.GetProperty("claudeAiOauth").GetProperty("accessToken").GetString();
        }
        catch { return null; }
    }
}
