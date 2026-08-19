using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace TomorrowOS.Player;

/// <summary>
/// Yes = exit app. No or window X = continue playing.
/// </summary>
internal sealed class ExitConfirmDialog : Window
{
    private bool _exitConfirmed;

    public ExitConfirmDialog()
    {
        Title = "TomorrowOS";
        Width = 420;
        Height = 180;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "Exit TomorrowOS player?",
            FontSize = 16,
            Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Yes = exit app\nNo / ✕ = continue",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var yes = new Button { Content = "Yes", Width = 88, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var no = new Button { Content = "No", Width = 88, Height = 32, IsCancel = true };

        yes.Click += (_, _) =>
        {
            _exitConfirmed = true;
            DialogResult = true;
            Close();
        };
        no.Click += (_, _) =>
        {
            _exitConfirmed = false;
            DialogResult = false;
            Close();
        };

        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        panel.Children.Add(buttons);
        Content = panel;

        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Title-bar X (and Esc via IsCancel) must mean "continue", never exit.
        if (!_exitConfirmed)
        {
            DialogResult = false;
        }
    }

    /// <returns>true when the user chose Yes (exit).</returns>
    public static bool ConfirmExit(Window? owner = null)
    {
        var dialog = new ExitConfirmDialog();
        if (owner != null)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true;
    }
}
