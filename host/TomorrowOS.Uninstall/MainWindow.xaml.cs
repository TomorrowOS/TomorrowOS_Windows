using System.Diagnostics;
using System.IO;
using System.Windows;

namespace TomorrowOS.Uninstall;

public partial class MainWindow : Window
{
    private readonly string _installDir;
    private readonly bool _runningFromTemp;

    public MainWindow()
    {
        InitializeComponent();

        var args = Environment.GetCommandLineArgs();
        _runningFromTemp = args.Any(a => a.Equals("/fromtemp", StringComparison.OrdinalIgnoreCase));
        _installDir = GetArg(args, "/dir") ?? UninstallService.DefaultInstallDir;

        if (_runningFromTemp)
        {
            Loaded += async (_, _) =>
            {
                ConfirmPane.Visibility = Visibility.Collapsed;
                ProgressPane.Visibility = Visibility.Visible;
                await RunUninstallAsync();
            };
        }
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            MessageBox.Show("Could not locate the uninstaller.", "TomorrowOS", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var installDir = Path.GetFullPath(_installDir);
        var exeDir = Path.GetFullPath(Path.GetDirectoryName(exe) ?? "");
        var insideInstall = exeDir.Equals(installDir, StringComparison.OrdinalIgnoreCase)
            || exeDir.StartsWith(installDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (insideInstall && !_runningFromTemp)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TomorrowOS-Uninstall");
            Directory.CreateDirectory(tempDir);
            var tempExe = Path.Combine(tempDir, Path.GetFileName(exe));
            File.Copy(exe, tempExe, overwrite: true);
            Process.Start(new ProcessStartInfo
            {
                FileName = tempExe,
                Arguments = $"/fromtemp /dir \"{installDir}\"",
                UseShellExecute = true,
                Verb = "runas"
            });
            Close();
            return;
        }

        ConfirmPane.Visibility = Visibility.Collapsed;
        ProgressPane.Visibility = Visibility.Visible;
        await RunUninstallAsync();
    }

    private async Task RunUninstallAsync()
    {
        var steps = new (string Label, Action Work)[]
        {
            ("Stopping watchdog restart…", UninstallService.WriteStopFlag),
            ("Stopping TomorrowOS…", UninstallService.StopTomorrowOsProcesses),
            ("Removing startup registration…", UninstallService.RemoveAutoStart),
            ("Removing desktop shortcut…", UninstallService.RemoveDesktopShortcut),
            ("Removing local data…", () =>
            {
                UninstallService.DeleteDirectoryBestEffort(UninstallService.ProgramDataRoot);
                UninstallService.DeleteDirectoryBestEffort(UninstallService.LocalAppDataRoot);
            }),
            ("Removing program files…", () => UninstallService.DeleteInstallDir(_installDir, Environment.ProcessPath)),
        };

        try
        {
            for (var i = 0; i < steps.Length; i++)
            {
                StatusText.Text = steps[i].Label;
                var target = (int)Math.Round((i + 1) * 100.0 / steps.Length);
                await Task.Run(steps[i].Work);
                await AnimateBarAsync(target);
            }

            Bar.Value = 100;
            StatusText.Text = "Finished.";
            await Task.Delay(250);
            ProgressPane.Visibility = Visibility.Collapsed;
            DonePane.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Uninstall could not finish:\n" + ex.Message,
                "TomorrowOS",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task AnimateBarAsync(int target)
    {
        var start = Bar.Value;
        if (target <= start)
        {
            Bar.Value = target;
            return;
        }

        var frames = 12;
        for (var f = 1; f <= frames; f++)
        {
            Bar.Value = start + (target - start) * f / frames;
            await Task.Delay(40);
        }

        Bar.Value = target;
    }
}
