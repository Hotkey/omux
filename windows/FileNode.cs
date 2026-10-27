using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;

namespace Omux;

/// <summary>A lazily expanded folder tree node. Identity is the path so expansion survives refreshes.</summary>
public sealed class FileNode : Observable
{
    public static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".git", ".build", "DerivedData", "__pycache__", ".venv", "venv", "bin", "obj" };

    private bool _isExpanded;
    private bool _loaded;
    private ImageSource? _icon;

    public string Path { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    /// <summary>A section heading ("Mentioned in session", the root folder), not a file.</summary>
    public bool IsSection { get; init; }
    /// <summary>Dim text after the name, e.g. the folder of a mentioned file.</summary>
    public string? Subtitle { get; init; }
    public ObservableCollection<FileNode> Children { get; } = [];

    public ImageSource? Icon => IsSection ? null : _icon ??= Desktop.Icon(Path, IsDirectory);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!Set(ref _isExpanded, value) || !value || _loaded || !IsDirectory) return;
            Load(null);
        }
    }

    public FileNode(string path, bool isDirectory, string? name = null)
    {
        Path = path;
        IsDirectory = isDirectory;
        Name = name ?? System.IO.Path.GetFileName(path.TrimEnd('\\'));
        if (IsDirectory) Children.Add(Placeholder);
    }

    private static FileNode Placeholder => new("", false, "");

    public static FileNode Section(string name, IEnumerable<FileNode> children)
    {
        var section = new FileNode("", false, name) { IsSection = true };
        foreach (var child in children) section.Children.Add(child);
        section._isExpanded = true;
        return section;
    }

    /// <summary>Lists the folder; children that were expanded before stay expanded.</summary>
    public void Load(HashSet<string>? expanded)
    {
        _loaded = true;
        Children.Clear();
        foreach (var child in List(Path))
        {
            if (expanded?.Contains(child.Path) == true) child.Restore(expanded);
            Children.Add(child);
        }
    }

    /// <summary>Re-expands a node that was open before a refresh.</summary>
    public void Restore(HashSet<string> expanded)
    {
        _isExpanded = true;
        Load(expanded);
    }

    public void CollectExpanded(HashSet<string> into)
    {
        if (_isExpanded && IsDirectory) into.Add(Path);
        foreach (var child in Children) child.CollectExpanded(into);
    }

    public static List<FileNode> List(string folder)
    {
        var byDate = Prefs.Current.FilesByDate;
        var entries = new List<(FileNode Node, DateTime Modified)>();
        try
        {
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if (Skipped.Contains(info.Name) || info.Name.StartsWith('.') || info.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                entries.Add((new FileNode(info.FullName, info is DirectoryInfo), info.LastWriteTime));
            }
        }
        catch { }
        return entries
            .OrderByDescending(e => e.Node.IsDirectory)
            .ThenBy(e => byDate ? -e.Modified.Ticks : 0)
            .ThenBy(e => e.Node.Name, NaturalComparer.Instance)
            .Take(500)
            .Select(e => e.Node)
            .ToList();
    }
}

/// <summary>Explorer's sort order: "file2" before "file10".</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    public int Compare(string? a, string? b) => StrCmpLogicalW(a ?? "", b ?? "");
}
