using Avalonia;

using Earmark.Desktop.Services;
using Earmark.Desktop.Styles;

namespace Earmark.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        UiWatchdog.WaitForPreviousInstance(args);
        var withoutFocus = StartupRegistration.HasArgument(args, StartupRegistration.TrayArgument) || StartupRegistration.HasArgument(args, StartupRegistration.NoActivateArgument);
        if (!SingleInstance.TryAcquire(showRunning: !withoutFocus, out var instance))
        {
            return 0;
        }

        App.Instance = instance;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            instance.Dispose();
        }

        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .UseEarmarkFonts()
        .With(new Win32PlatformOptions
        {
            RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
            CompositionMode = [Win32CompositionMode.WinUIComposition, Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface],
        });
}
