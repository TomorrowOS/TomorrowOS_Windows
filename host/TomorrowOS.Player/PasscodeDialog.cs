using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TomorrowOS.Player;

/// <summary>
/// Non-modal floating maintenance passcode gate.
/// Exempt from player hardening: always stays above the Topmost player window.
/// </summary>
internal sealed class PasscodeDialog : Window
{
    private readonly PasswordBox _box;
    private readonly TextBlock _error;
    private readonly Func<string, bool> _validate;
    private readonly MaintenanceWindowZOrder _zOrder;
    private bool _accepted;

    public event Action? Unlocked;
    public event Action? Cancelled;

    private PasscodeDialog(Func<string, bool> validate)
    {
        _validate = validate;
        _zOrder = new MaintenanceWindowZOrder(this);

        Title = "TomorrowOS";
        Width = 420;
        Height = 210;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowActivated = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "Enter maintenance passcode",
            FontSize = 16,
            Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "This device passcode was set during installation.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        _box = new PasswordBox
        {
            Height = 32,
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 14
        };
        panel.Children.Add(_box);

        _error = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.Firebrick,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };
        panel.Children.Add(_error);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var ok = new Button { Content = "Unlock", Width = 88, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 88, Height = 32, IsCancel = true };

        ok.Click += (_, _) => Submit();
        cancel.Click += (_, _) => Dismiss(false);
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Submit();
                e.Handled = true;
            }
        };

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        PreviewKeyDown += (_, e) =>
        {
            if (IsMaintenanceChord(e))
            {
                e.Handled = true;
            }
        };

        Closed += (_, _) =>
        {
            _zOrder.Stop();
            if (!_accepted)
            {
                Cancelled?.Invoke();
            }
        };

        Loaded += (_, _) =>
        {
            _zOrder.Start();
            Activate();
            _box.Focus();
            Keyboard.Focus(_box);
        };
    }

    private static bool IsMaintenanceChord(KeyEventArgs e)
    {
        if (e.Key != Key.M)
        {
            return false;
        }

        return (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) &&
               (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) &&
               (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt));
    }

    private void Dismiss(bool unlocked)
    {
        if (unlocked)
        {
            _accepted = true;
            Unlocked?.Invoke();
        }

        Close();
    }

    private void Submit()
    {
        var pass = _box.Password ?? "";
        if (string.IsNullOrWhiteSpace(pass))
        {
            ShowError("Passcode is required.");
            return;
        }

        if (!_validate(pass))
        {
            ShowError("Incorrect passcode. Try again.");
            return;
        }

        Dismiss(true);
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
        _box.Clear();
        _box.Focus();
    }

    public static PasscodeDialog ShowFloating(Func<string, bool> validate)
    {
        var dialog = new PasscodeDialog(validate);
        dialog.Show();
        return dialog;
    }
}
