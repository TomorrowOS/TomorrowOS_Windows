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
        MinWidth = 420;
        MaxWidth = 420;
        // Grow with content so the error line never clips Exit / Cancel.
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowActivated = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var root = new DockPanel { Margin = new Thickness(20) };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var ok = new Button { Content = "Exit", Width = 88, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 88, Height = 32, IsCancel = true };

        ok.Click += (_, _) => Submit();
        cancel.Click += (_, _) => Dismiss(false);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Enter maintenance passcode to exit",
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
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Submit();
                e.Handled = true;
            }
        };

        // Always reserve one line so showing an error does not reflow over the buttons.
        _error = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.Firebrick,
            Margin = new Thickness(0, 8, 0, 0),
            MinHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Hidden
        };
        panel.Children.Add(_error);
        root.Children.Add(panel);
        Content = root;

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
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is not (Key.M or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt))
        {
            return false;
        }

        return MaintenanceHotkeyHook.IsChordPhysicallyDown();
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
