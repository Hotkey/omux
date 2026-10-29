using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Omux;

/// <summary>A subagent's transcript, shown where the file preview usually is.</summary>
public partial class AgentTranscriptView : UserControl
{
    private readonly AgentRef _ref;
    private readonly AgentTranscript _transcript;
    private readonly DispatcherTimer _clock;
    private readonly TabItem? _tab;

    public AgentTranscriptView(AgentRef agentRef)
    {
        InitializeComponent();
        _ref = agentRef;
        _tab = Store.Shared.Tab(agentRef.TabId);
        Description.Text = agentRef.Description;
        AgentType.Text = agentRef.Type;
        Permission.TabId = agentRef.TabId;
        _transcript = new AgentTranscript(agentRef.Transcript);
        Entries.ItemsSource = _transcript.Entries;
        // New entries scroll into view, unless the user scrolled up to read.
        _transcript.Entries.CollectionChanged += (_, _) =>
        {
            if (Scroll.VerticalOffset >= Scroll.ScrollableHeight - 24) Dispatcher.BeginInvoke(Scroll.ScrollToEnd, DispatcherPriority.Background);
        };
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateLive();
        Loaded += (_, _) =>
        {
            _clock.Start();
            if (_tab is not null) _tab.Runtime.PropertyChanged += OnRuntimeChanged;
            UpdateLive();
            Dispatcher.BeginInvoke(Scroll.ScrollToEnd, DispatcherPriority.Background);
        };
        Unloaded += (_, _) =>
        {
            _clock.Stop();
            _transcript.Dispose();
            if (_tab is not null) _tab.Runtime.PropertyChanged -= OnRuntimeChanged;
        };
    }

    public string AgentId => _ref.AgentId;

    private void OnRuntimeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabRuntime.Agents) or nameof(TabRuntime.AwaitsPermission)) UpdateLive();
    }

    /// <summary>The dot, the clock and Allow/Deny follow the agent while the monitor still lists it.</summary>
    private void UpdateLive()
    {
        var live = _tab?.Runtime.Agents.FirstOrDefault(a => a.Id == _ref.AgentId);
        Dot.Visibility = live is null ? Visibility.Collapsed : Visibility.Visible;
        if (live is not null)
        {
            Dot.Stroke = live.Brush;
            Dot.Fill = live.DotFill;
        }
        Stats.Text = live is null ? "" : $"{live.ElapsedText} · {live.ToolCount} tools";
        Permission.Visibility = live?.Blocked == true && _tab?.Runtime.AwaitsPermission == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Store.Shared.OpenAgent = null;
}
