using System.Windows;
using System.Windows.Controls;

namespace Omux;

/// <summary>Allow / Deny for Claude Code's permission prompt, without switching to the session.</summary>
public sealed class PermissionButtons : StackPanel
{
    public static readonly DependencyProperty TabIdProperty =
        DependencyProperty.Register(nameof(TabId), typeof(Guid), typeof(PermissionButtons));

    public Guid TabId { get => (Guid)GetValue(TabIdProperty); set => SetValue(TabIdProperty, value); }

    public PermissionButtons()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Children.Add(Make("Allow", allow: true));
        Children.Add(Make("Deny", allow: false));
    }

    private Button Make(string title, bool allow)
    {
        var button = new Button
        {
            Content = title,
            FontSize = 11,
            Padding = new Thickness(10, 1, 10, 2),
            MinHeight = 0,
            MinWidth = 0,
            Focusable = false,
            Margin = new Thickness(allow ? 0 : 6, 0, 0, 0),
        };
        if (allow) button.SetResourceReference(StyleProperty, "AccentButtonStyle");
        button.Click += (_, e) =>
        {
            e.Handled = true;
            Store.Shared.AnswerPermission(TabId, allow);
        };
        return button;
    }
}
