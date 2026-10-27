using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Omux;

public sealed record Theme(string Id, string Label, uint Background, uint Chrome, uint Foreground, uint[] Palette)
{
    private static readonly uint[] DefaultPalette =
        [0x000000, 0xCD3131, 0x0DBC79, 0xE5E510, 0x2472C8, 0xBC3FBC, 0x11A8CD, 0xE5E5E5,
         0x666666, 0xF14C4C, 0x23D18B, 0xF5F543, 0x3B8EEA, 0xD670D6, 0x29B8DB, 0xFFFFFF];

    private static readonly uint[] SolarizedPalette =
        [0x073642, 0xDC322F, 0x859900, 0xB58900, 0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5,
         0x002B36, 0xCB4B16, 0x586E75, 0x657B83, 0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3];

    /// <summary>Same themes and colours as the macOS app.</summary>
    public static readonly Theme[] All =
    [
        new("black", "Black", 0x000000, 0x0F0F0F, 0xE6E6E6, DefaultPalette),
        new("dark", "Dark", 0x1C1C1F, 0x252529, 0xE6E6E6, DefaultPalette),
        new("graphite", "Graphite", 0x2B2B2B, 0x363636, 0xE6E6E6, DefaultPalette),
        new("midnight", "Midnight", 0x0D1117, 0x161B22, 0xC9D1D9,
            [0x484F58, 0xFF7B72, 0x3FB950, 0xD29922, 0x58A6FF, 0xBC8CFF, 0x39C5CF, 0xB1BAC4,
             0x6E7681, 0xFFA198, 0x56D364, 0xE3B341, 0x79C0FF, 0xD2A8FF, 0x56D4DD, 0xF0F6FC]),
        new("dracula", "Dracula", 0x282A36, 0x21222C, 0xF8F8F2,
            [0x21222C, 0xFF5555, 0x50FA7B, 0xF1FA8C, 0xBD93F9, 0xFF79C6, 0x8BE9FD, 0xF8F8F2,
             0x6272A4, 0xFF6E6E, 0x69FF94, 0xFFFFA5, 0xD6ACFF, 0xFF92DF, 0xA4FFFF, 0xFFFFFF]),
        new("nord", "Nord", 0x2E3440, 0x3B4252, 0xD8DEE9,
            [0x3B4252, 0xBF616A, 0xA3BE8C, 0xEBCB8B, 0x81A1C1, 0xB48EAD, 0x88C0D0, 0xE5E9F0,
             0x4C566A, 0xBF616A, 0xA3BE8C, 0xEBCB8B, 0x81A1C1, 0xB48EAD, 0x8FBCBB, 0xECEFF4]),
        new("oneDark", "One Dark", 0x282C34, 0x21252B, 0xABB2BF,
            [0x282C34, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xABB2BF,
             0x5C6370, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xFFFFFF]),
        new("gruvbox", "Gruvbox", 0x282828, 0x1D2021, 0xEBDBB2,
            [0x282828, 0xCC241D, 0x98971A, 0xD79921, 0x458588, 0xB16286, 0x689D6A, 0xA89984,
             0x928374, 0xFB4934, 0xB8BB26, 0xFABD2F, 0x83A598, 0xD3869B, 0x8EC07C, 0xEBDBB2]),
        new("solarizedDark", "Solarized Dark", 0x002B36, 0x073642, 0x839496, SolarizedPalette),
        new("light", "Light", 0xFBFBFB, 0xEFEFF1, 0x1F1F1F,
            [0x000000, 0xC91B00, 0x00A600, 0x9A7B00, 0x0225C7, 0xCA30C7, 0x00A2B2, 0xBFBFBF,
             0x686868, 0xFF6E67, 0x2EB84A, 0xC7A500, 0x6871FF, 0xFF77FF, 0x1BA8B5, 0xFFFFFF]),
        new("solarizedLight", "Solarized Light", 0xFDF6E3, 0xEEE8D5, 0x657B83, SolarizedPalette),
    ];

    public static Theme Current => All.FirstOrDefault(t => t.Id == Prefs.Current.Theme) ?? All[1];

    public bool IsLight => Id is "light" or "solarizedLight";

    private static string Css(uint hex) => "#" + hex.ToString("X6", CultureInfo.InvariantCulture);

    private static readonly string[] AnsiNames =
        ["black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
         "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite"];

    /// <summary>An xterm.js ITheme.</summary>
    public Dictionary<string, string> ToXterm()
    {
        var theme = new Dictionary<string, string>
        {
            ["background"] = Css(Background),
            ["foreground"] = Css(Foreground),
            ["cursor"] = Css(Foreground),
            ["cursorAccent"] = Css(Background),
            ["selectionBackground"] = IsLight ? "#0000002E" : "#FFFFFF40",
        };
        for (var i = 0; i < AnsiNames.Length; i++) theme[AnsiNames[i]] = Css(Palette[i]);
        return theme;
    }

    /// <summary>Pushes the theme into the brushes every window binds through DynamicResource.</summary>
    public void ApplyToResources(ResourceDictionary resources)
    {
        var fg = TabColors.Hex(Foreground);
        Color Fade(double alpha) => Color.FromArgb((byte)(alpha * 255), fg.R, fg.G, fg.B);
        resources["BackgroundBrush"] = Brush(TabColors.Hex(Background));
        resources["ChromeBrush"] = Brush(TabColors.Hex(Chrome));
        resources["ForegroundBrush"] = Brush(fg);
        resources["SecondaryBrush"] = Brush(Fade(0.62));
        resources["TertiaryBrush"] = Brush(Fade(0.40));
        resources["SeparatorBrush"] = Brush(Fade(0.08));
        resources["HoverBrush"] = Brush(Fade(0.06));
        resources["SelectionBrush"] = Brush(Fade(0.13));
        resources["FieldBrush"] = Brush(Fade(0.07));
        resources["DimDotBrush"] = Brush(Fade(0.35));
        resources["AccentBrush"] = Brush(TabColors.Hex(IsLight ? 0x0A64D2u : 0x3B8EEAu));
    }

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

public static class Fonts
{
    /// <summary>Installed fixed-pitch families, for the Settings picker.</summary>
    public static IReadOnlyList<string> MonospaceFamilies { get; } = LoadMonospace();

    private static List<string> LoadMonospace()
    {
        var result = new List<string>();
        foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
        {
            try
            {
                var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                if (!typeface.TryGetGlyphTypeface(out var glyphs)) continue;
                double Width(char c) => glyphs.CharacterToGlyphMap.TryGetValue(c, out var g) ? glyphs.AdvanceWidths[g] : -1;
                if (Width('i') > 0 && Width('i') == Width('W') && Width('W') == Width('.'))
                    result.Add(family.Source);
            }
            catch { }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>The terminal font family: the Settings choice, else the system's monospaced font.</summary>
    public static string TerminalFamily
    {
        get
        {
            var chosen = Prefs.Current.FontFamily;
            return chosen.Length > 0 ? $"'{chosen}', 'Cascadia Mono', Consolas, monospace" : "'Cascadia Mono', Consolas, monospace";
        }
    }

    public static double TerminalSize => Prefs.Current.FontSize > 0 ? Prefs.Current.FontSize : 13;
}
