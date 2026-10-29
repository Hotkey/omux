using System.IO;
using System.IO.Pipes;
using System.Windows;

namespace Omux;

public partial class App : Application
{
    private static readonly string InstanceName = $"omux-{Environment.UserName}";
    private Mutex? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // One omux per user: a second launch (Explorer's "Open in omux", `omux <folder>`)
        // hands its folders to the running window and exits.
        _instance = new Mutex(true, InstanceName, out var first);
        if (!first)
        {
            ForwardToRunningInstance(e.Args);
            Shutdown();
            return;
        }

        ApplyTheme();
        Notifier.Activate();
        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
        _ = ListenForOtherInstances(window);
    }

    /// <summary>Applies the Settings theme to every window's brushes and to the Fluent control styles.</summary>
    public static void ApplyTheme()
    {
        var theme = Theme.Current;
#pragma warning disable WPF0001 // ThemeMode is marked experimental.
        Current.ThemeMode = theme.IsLight ? ThemeMode.Light : ThemeMode.Dark;
#pragma warning restore WPF0001
        theme.ApplyToResources(Current.Resources);
        foreach (Window window in Current.Windows) Desktop.StyleTitleBar(window, theme);
    }

    private static void ForwardToRunningInstance(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", InstanceName, PipeDirection.Out);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe);
            writer.Write(string.Join('\n', args.Select(Path.GetFullPath)));
        }
        catch { }
    }

    private static async Task ListenForOtherInstances(MainWindow window)
    {
        while (true)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(InstanceName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync();
                using var reader = new StreamReader(pipe);
                var folders = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                window.Dispatcher.Invoke(() => window.OpenFromOutside(folders));
            }
            catch { await Task.Delay(500); }
        }
    }
}
