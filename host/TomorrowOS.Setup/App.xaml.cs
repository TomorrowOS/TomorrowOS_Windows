using System.Windows;

namespace TomorrowOS.Setup;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Splash close must not shut down the app before the real installer window opens.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var silent = e.Args.Any(a =>
            a.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--silent", StringComparison.OrdinalIgnoreCase));

        if (silent)
        {
            try
            {
                SilentInstaller.Run(e.Args);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "TomorrowOS silent install failed");
                Shutdown(1);
            }

            return;
        }

        var splash = new SplashWindow();
        var proceed = splash.ShowDialog() == true;
        if (!proceed)
        {
            Shutdown(0);
            return;
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
