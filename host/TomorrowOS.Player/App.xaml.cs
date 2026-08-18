using System.IO;
using System.Windows;

namespace TomorrowOS.Player;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppPaths.LogDirectory, "player-crash.log"),
                    $"[{DateTime.Now:O}] {args.Exception}\n");
            }
            catch
            {
                // ignore logging failures
            }
        };
    }
}
