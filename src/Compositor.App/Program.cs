using Avalonia;
using Compositor.App.Diagnostics;

namespace Compositor.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppLog.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("UnhandledException", "Unhandled application exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("UnobservedTask", "Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        try
        {
            AppLog.Info("Application", $"Arguments: {string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            AppLog.Error("Startup", "Fatal application error", e);
            return 1;
        }
        finally
        {
            AppLog.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
