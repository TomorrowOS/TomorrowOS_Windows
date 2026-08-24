using System.IO;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace TomorrowOS.Setup;

public partial class SplashWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private bool _completed;

    public SplashWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunAsync(_cts.Token);
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            _completed = true;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            // Closed by the user — do not open the setup UI.
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Setup could not start:\n" + ex.Message,
                "TomorrowOS Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            if (IsVisible)
            {
                DialogResult = false;
            }
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_completed)
        {
            return;
        }

        _cts.Cancel();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var steps = new (string Label, Func<CancellationToken, Task> Work)[]
        {
            ("Preparing…", PrepareAsync),
            ("Extracting…", ExtractAsync),
            ("Initialising setup…", InitialiseAsync),
        };

        for (var i = 0; i < steps.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            StatusText.Text = steps[i].Label;
            await steps[i].Work(ct);
            await AnimateBarAsync((i + 1) * 100.0 / steps.Length, ct);
        }

        Bar.Value = 100;
        StatusText.Text = "Starting setup…";
        await Task.Delay(180, ct);
    }

    private static async Task PrepareAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = AppContext.BaseDirectory;
        await Task.Delay(450, ct);
    }

    private static async Task ExtractAsync(CancellationToken ct)
    {
        var root = AppContext.BaseDirectory;
        var payload = Path.Combine(root, "payload");
        var www = Path.Combine(root, "wwwroot");

        if (Directory.Exists(payload))
        {
            foreach (var _ in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
            }
        }

        if (Directory.Exists(www))
        {
            foreach (var _ in Directory.EnumerateFiles(www, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
            }
        }

        await Task.Delay(700, ct);
    }

    private static async Task InitialiseAsync(CancellationToken ct)
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TomorrowOS",
            "setup-webview");
        Directory.CreateDirectory(userData);
        await Task.Delay(500, ct);
    }

    private async Task AnimateBarAsync(double target, CancellationToken ct)
    {
        var start = Bar.Value;
        if (target <= start)
        {
            Bar.Value = target;
            return;
        }

        const int frames = 14;
        for (var f = 1; f <= frames; f++)
        {
            ct.ThrowIfCancellationRequested();
            Bar.Value = start + (target - start) * f / frames;
            await Task.Delay(36, ct);
        }

        Bar.Value = target;
    }
}
