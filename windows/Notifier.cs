using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Omux;

/// <summary>
/// Windows notifications for sessions you aren't looking at. Clicking one opens its session.
/// Each session has at most one in Action Center; a new one replaces it.
/// </summary>
public static class Notifier
{
    private const string Group = "omux";

    /// <summary>Call at launch, so a click on a notification reaches the store.</summary>
    public static void Activate()
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += e =>
            {
                if (!ToastArguments.Parse(e.Argument).TryGetValue("tabID", out var raw) || !Guid.TryParse(raw, out var id)) return;
                Application.Current.Dispatcher.BeginInvoke(() => Store.Shared.Reveal(id));
            };
        }
        catch { }
    }

    /// <summary>Shows a notification for a session, replacing the one it showed before.</summary>
    public static void Post(Guid tabId, string title, string? subtitle, string body)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument("tabID", tabId.ToString())
                .AddText(title)
                .AddText(body);
            if (!string.IsNullOrEmpty(subtitle)) builder.AddAttributionText(subtitle);
            builder.Show(toast =>
            {
                toast.Tag = Tag(tabId);
                toast.Group = Group;
            });
        }
        catch { }
    }

    /// <summary>Removes a session's notification from Action Center once you've seen the session.</summary>
    public static void Clear(Guid tabId)
    {
        try { ToastNotificationManagerCompat.History.Remove(Tag(tabId), Group); }
        catch { }
    }

    private static string Tag(Guid tabId) => tabId.ToString("N");
}
