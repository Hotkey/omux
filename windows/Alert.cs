using System.Windows;
using System.Windows.Controls;

namespace Omux;

/// <summary>
/// A message with buttons of its own, the counterpart of NSAlert: MessageBox only offers
/// OK / Yes / No / Cancel. The first button is the default, a "Cancel" button answers Esc.
/// </summary>
public sealed class Alert : Window
{
    private readonly TextBox? _field;
    private int _choice = -1;

    private Alert(string title, string message, IReadOnlyList<string> buttons, string? input)
    {
        Title = "omux";
        // As wide as the buttons need, within bounds; the text wraps.
        MinWidth = 420;
        MaxWidth = 640;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "ChromeBrush");
        SetResourceReference(ForegroundProperty, "ForegroundBrush");
        SourceInitialized += (_, _) => Desktop.StyleTitleBar(this, Theme.Current);

        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 560 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (message.Length > 0)
        {
            var detail = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
            panel.Children.Add(detail);
        }
        if (input is not null)
        {
            _field = new TextBox { Text = input, Margin = new Thickness(0, 14, 0, 0) };
            panel.Children.Add(_field);
            Loaded += (_, _) => { _field.Focus(); _field.SelectAll(); };
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var button = new Button { Content = buttons[i], MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), IsDefault = i == 0, IsCancel = buttons[i] == "Cancel" };
            if (i == 0) button.SetResourceReference(StyleProperty, "AccentButtonStyle");
            button.Click += (_, _) => { _choice = index; Close(); };
            row.Children.Add(button);
        }
        panel.Children.Add(row);
        Content = panel;
    }

    /// <summary>Shows the alert over the active window; returns the clicked button's index, or -1 when closed.</summary>
    public static int Show(string title, string message, params string[] buttons) => Ask(title, message, null, buttons).Choice;

    /// <summary>The same, with a text field that starts with input.</summary>
    public static (int Choice, string Text) Ask(string title, string message, string? input, params string[] buttons)
    {
        var alert = new Alert(title, message, buttons.Length > 0 ? buttons : ["OK"], input);
        alert.Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w != alert) ?? Application.Current.MainWindow;
        alert.ShowDialog();
        return (alert._choice, alert._field?.Text ?? "");
    }
}
