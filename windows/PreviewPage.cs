using System.Globalization;
using System.IO;
using System.Net;
using Markdig;

namespace Omux;

/// <summary>
/// Builds the page the files panel shows for a file: Markdown, code and text, images,
/// audio, video and PDFs. Stands in for Quick Look, PDFKit and AVKit on macOS.
/// </summary>
public static class PreviewPage
{
    public const string PageHost = "omux.app";
    public const string FilesHost = "omux.preview";
    public const string Origin = "https://" + PageHost;

    /// <summary>The page served at the next preview URL.</summary>
    public static string Html { get; set; } = "";

    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".avif" };
    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".webm" };
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".wav", ".aac", ".ogg", ".flac" };
    private static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".json", ".swift", ".py", ".js", ".ts", ".tsx", ".jsx", ".html", ".css", ".yaml", ".yml", ".toml", ".sh", ".zsh",
        ".log", ".csv", ".xml", ".rs", ".go", ".c", ".h", ".cpp", ".java", ".kt", ".rb", ".sql", ".env", ".gitignore", ".lock",
        ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".xaml", ".ps1", ".psm1", ".bat", ".cmd", ".ini", ".cfg", ".config",
    };

    private static readonly MarkdownPipeline Markdown = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    private static bool IsMarkdown(string path) => Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown";

    /// <summary>Text-like files get the wrap toggle. Unknown extensions count as text when they hold no NUL bytes.</summary>
    public static bool IsText(string path)
    {
        var ext = Path.GetExtension(path);
        if (IsMarkdown(path) || Text.Contains(ext) || Path.GetFileName(path).StartsWith('.')) return true;
        if (Images.Contains(ext) || Video.Contains(ext) || Audio.Contains(ext) || ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 1_000_000) return false;
            var head = new byte[8192];
            var read = stream.Read(head, 0, head.Length);
            return Array.IndexOf(head, (byte)0, 0, read) < 0;
        }
        catch { return false; }
    }

    /// <summary>PDFs open straight in the browser's viewer; everything else gets a generated page.</summary>
    public static string UrlFor(string path) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? $"https://{FilesHost}/{Uri.EscapeDataString(Path.GetFileName(path))}"
            : $"{Origin}/preview.html?f={Uri.EscapeDataString(path)}&t={DateTime.UtcNow.Ticks}";

    public static string Render(string path, Theme theme, bool wrap)
    {
        var ext = Path.GetExtension(path);
        var source = $"https://{FilesHost}/{Uri.EscapeDataString(Path.GetFileName(path))}";
        string kind, body;
        if (IsMarkdown(path))
        {
            kind = "md";
            body = Markdig.Markdown.ToHtml(ReadText(path), Markdown);
        }
        else if (Images.Contains(ext))
        {
            kind = "image";
            body = $"<img src=\"{source}\">";
        }
        else if (Video.Contains(ext))
        {
            kind = "media";
            body = $"<video controls src=\"{source}\"></video>";
        }
        else if (Audio.Contains(ext))
        {
            kind = "media";
            body = $"<audio controls src=\"{source}\"></audio>";
        }
        else if (IsText(path))
        {
            kind = "text";
            body = $"<pre>{WebUtility.HtmlEncode(ReadText(path))}</pre>";
        }
        else
        {
            kind = "empty";
            body = "<p>No preview for this kind of file.</p>";
        }

        static string Css(uint hex) => "#" + hex.ToString("X6", CultureInfo.InvariantCulture);
        var fg = Css(theme.Foreground);
        var whiteSpace = wrap ? "pre-wrap" : "pre";
        return $$"""
            <!doctype html>
            <html><head><meta charset="utf-8"><base href="https://{{FilesHost}}/">
            <style>
              :root { color-scheme: {{(theme.IsLight ? "light" : "dark")}}; }
              html, body { margin: 0; background: {{Css(theme.Background)}}; color: {{fg}}; }
              body { font: 13px/1.5 'Segoe UI Variable Text', 'Segoe UI', sans-serif; }
              code, pre { font-family: 'Cascadia Mono', Consolas, monospace; font-size: 11px; }
              .text pre { margin: 0; padding: 12px; white-space: {{whiteSpace}}; overflow-wrap: anywhere; }
              .md { padding: 16px; max-width: 900px; }
              .md h1 { font-size: 22px; } .md h2 { font-size: 18px; } .md h3 { font-size: 15px; } .md h4, .md h5, .md h6 { font-size: 13px; }
              .md pre { padding: 10px; border-radius: 6px; background: color-mix(in srgb, {{fg}} 6%, transparent); white-space: {{whiteSpace}}; overflow-x: auto; }
              .md :not(pre) > code { padding: 1px 4px; border-radius: 4px; background: color-mix(in srgb, {{fg}} 8%, transparent); }
              .md blockquote { margin: 0; padding-left: 10px; border-left: 3px solid color-mix(in srgb, {{fg}} 40%, transparent); opacity: .75; }
              .md table { border-collapse: collapse; } .md td, .md th { border: 1px solid color-mix(in srgb, {{fg}} 15%, transparent); padding: 4px 8px; }
              .md hr { border: 0; border-top: 1px solid color-mix(in srgb, {{fg}} 15%, transparent); }
              .md img { max-width: 100%; }
              a { color: {{(theme.IsLight ? "#0A64D2" : "#3B8EEA")}}; }
              .image, .media { height: 100vh; box-sizing: border-box; padding: 12px; display: flex; align-items: center; justify-content: center; }
              .image img, .media video { max-width: 100%; max-height: 100%; object-fit: contain; }
              .media audio { width: 100%; }
              .empty { height: 100vh; display: flex; align-items: center; justify-content: center; font-size: 11px; opacity: .6; }
            </style></head>
            <body class="{{kind}}">{{body}}</body></html>
            """;
    }

    private static string ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception error) { return error.Message; }
    }

    /// <summary>"12 KB", like macOS's ByteCountFormatter.</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return unit == 0 ? $"{bytes} bytes" : value.ToString(value < 10 ? "0.#" : "0", CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
