using System.Windows;
using System.Windows.Controls;

namespace TomorrowOS.Player;

internal sealed class InputDialog : Window
{
    private readonly TextBox _box;

    public string Response => _box.Text;

    public InputDialog(string prompt, string title, string defaultResponse)
    {
        Title = title;
        Width = 420;
        Height = 180;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        _box = new TextBox { Text = defaultResponse };
        panel.Children.Add(_box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }

    public static string Prompt(string prompt, string title, string defaultResponse = "")
    {
        var dialog = new InputDialog(prompt, title, defaultResponse);
        return dialog.ShowDialog() == true ? dialog.Response : string.Empty;
    }
}
