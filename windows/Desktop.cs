using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Omux;

/// <summary>Windows shell integration: opening things, Explorer, icons, the title bar.</summary>
public static class Desktop
{
    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "omux.exe");

    /// <summary>Opens a file, folder or URL with its default app.</summary>
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { }
    }

    public static void RevealInExplorer(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch { }
    }

    public static string? ChooseFolder(string? current = null, bool multiple = false, Window? owner = null) =>
        ChooseFolders(current, multiple, owner)?.FirstOrDefault();

    public static string[]? ChooseFolders(string? current = null, bool multiple = true, Window? owner = null)
    {
        var dialog = new OpenFolderDialog { Multiselect = multiple };
        if (!string.IsNullOrEmpty(current)) dialog.InitialDirectory = Paths.Expand(current);
        return dialog.ShowDialog(owner ?? Application.Current.MainWindow) == true ? dialog.FolderNames : null;
    }

    // MARK: Explorer's "Open in omux"

    private const string FolderKey = @"Software\Classes\Directory\shell\omux";
    private const string BackgroundKey = @"Software\Classes\Directory\Background\shell\omux";

    /// <summary>Whether Explorer's right-click menu on folders has "Open in omux".</summary>
    public static bool ExplorerMenuInstalled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(FolderKey + @"\command");
            return key?.GetValue(null) is string command && command.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            if (value)
            {
                Register(FolderKey, "%1");
                Register(BackgroundKey, "%V");
            }
            else
            {
                Registry.CurrentUser.DeleteSubKeyTree(FolderKey, throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(BackgroundKey, throwOnMissingSubKey: false);
            }
        }
    }

    private static void Register(string keyPath, string argument)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(null, "Open in omux");
        key.SetValue("Icon", $"\"{ExecutablePath}\",0");
        using var command = key.CreateSubKey("command");
        command.SetValue(null, $"\"{ExecutablePath}\" \"{argument}\"");
    }

    // MARK: Icons

    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The small shell icon Explorer shows for a file or folder.</summary>
    public static ImageSource? Icon(string path, bool isDirectory)
    {
        var key = isDirectory ? "<dir>" : Path.GetExtension(path) is { Length: > 0 } ext && ext != ".exe" && ext != ".ico" && ext != ".lnk" ? ext : path;
        if (IconCache.TryGetValue(key, out var cached)) return cached;
        ImageSource? image = null;
        var info = new ShFileInfo();
        var flags = ShgfiIcon | ShgfiSmallIcon | ShgfiUseFileAttributes;
        var attributes = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
        if (SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags) != IntPtr.Zero && info.hIcon != IntPtr.Zero)
        {
            image = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            DestroyIcon(info.hIcon);
        }
        IconCache[key] = image;
        return image;
    }

    // MARK: Title bar

    /// <summary>Colours the window's title bar like the sidebar (Windows 11; dark mode on Windows 10).</summary>
    public static void StyleTitleBar(Window window, Theme theme)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = theme.IsLight ? 0 : 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        var chrome = TabColors.Hex(theme.Chrome);
        var fg = TabColors.Hex(theme.Foreground);
        var caption = chrome.R | (chrome.G << 8) | (chrome.B << 16);
        var text = fg.R | (fg.G << 8) | (fg.B << 16);
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
    }

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const uint ShgfiIcon = 0x100, ShgfiSmallIcon = 0x1, ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeDirectory = 0x10, FileAttributeNormal = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
