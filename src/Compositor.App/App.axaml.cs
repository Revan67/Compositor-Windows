using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Compositor.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                var args = desktop.Args ?? [];
                if (args.Length == 0 && await window.RecoverIfAvailableAsync())
                {
                    return;
                }

                window.OpenFromCommandLine(args);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
