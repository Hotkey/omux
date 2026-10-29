using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Omux;

/// <summary>User settings, the counterpart of the macOS app's UserDefaults keys.</summary>
public sealed class Prefs : Observable
{
    public static Prefs Current { get; } = Load();

    private static string FilePath => Path.Combine(Persistence.Directory, "settings.json");

    private bool _confirmQuit = true;
    private bool _showFolderPath = true;
    private double _usageInterval = 300;
    private string _theme = "dark";
    private string _fontFamily = "";
    private double _fontSize = 13;
    private bool _autoResume;
    private bool _showFiles;
    private bool _filesByDate;
    private bool _filesBrowserCollapsed;
    private bool _previewWrapLines = true;
    private string _shell = "";
    private PanelTab _panelTab = PanelTab.Files;
    private bool _notifyNeedsYou = true;
    private bool _notifyDone;
    private bool _notifyPrograms = true;

    public bool ConfirmQuit { get => _confirmQuit; set => Set(ref _confirmQuit, value); }
    public bool ShowFolderPath { get => _showFolderPath; set => Set(ref _showFolderPath, value); }
    public double UsageInterval { get => _usageInterval; set => Set(ref _usageInterval, value); }
    public string Theme { get => _theme; set => Set(ref _theme, value); }
    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value); }
    public double FontSize { get => _fontSize; set => Set(ref _fontSize, value); }
    public bool AutoResume { get => _autoResume; set => Set(ref _autoResume, value); }
    public bool ShowFiles { get => _showFiles; set => Set(ref _showFiles, value); }
    public bool FilesByDate { get => _filesByDate; set => Set(ref _filesByDate, value); }
    public bool FilesBrowserCollapsed { get => _filesBrowserCollapsed; set => Set(ref _filesBrowserCollapsed, value); }
    public bool PreviewWrapLines { get => _previewWrapLines; set => Set(ref _previewWrapLines, value); }
    /// <summary>Shell executable for sessions; empty picks PowerShell 7, else Windows PowerShell.</summary>
    public string Shell { get => _shell; set => Set(ref _shell, value); }
    /// <summary>The right panel's tab: Files, Changes or Browser.</summary>
    public PanelTab PanelTab { get => _panelTab; set => Set(ref _panelTab, value); }
    public bool NotifyNeedsYou { get => _notifyNeedsYou; set => Set(ref _notifyNeedsYou, value); }
    public bool NotifyDone { get => _notifyDone; set => Set(ref _notifyDone, value); }
    public bool NotifyPrograms { get => _notifyPrograms; set => Set(ref _notifyPrograms, value); }

    /// <summary>Last window placement, restored at launch.</summary>
    public WindowPlacement? Window { get; set; }

    private static Prefs Load()
    {
        Prefs prefs;
        try { prefs = JsonSerializer.Deserialize<Prefs>(File.ReadAllText(FilePath)) ?? new Prefs(); }
        catch { prefs = new Prefs(); }
        prefs.PropertyChanged += (_, _) => prefs.Save();
        return prefs;
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}

public enum PanelTab { Files, Changes, Browser }

public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);
