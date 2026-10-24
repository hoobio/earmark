using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using Earmark.Desktop.Hosting;
using Earmark.Desktop.Services;
using Earmark.Desktop.Styles;
using Earmark.Desktop.Views;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Earmark.Desktop;

public sealed partial class App : Application
{
    private IHost? _host;

    public static SingleInstance? Instance { get; set; }

    public static IServiceProvider Services => (Current as App)?._host?.Services ?? throw new InvalidOperationException("Host is not yet initialized.");

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        DialogResourceScope.Register();
        ScrollIndicator.Register();
        AppFonts.UseGrayscaleText();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || Instance is not { } instance)
        {
            return;
        }

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _host = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true }).AddEarmark().Build();
        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        desktop.Exit += (_, _) =>
        {
            logger.LogInformation("Shutting down host");
            _host.Dispose();
        };

        logger.LogInformation("Earmark starting; log file: {LogPath}", AppInfo.CurrentLogPath);
        logger.LogInformation("Earmark {Version} ({Commit}) on {Os}, {Arch}, packaged {Packaged}", AppInfo.DisplayVersion, AppInfo.ShortCommit ?? "no commit", RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture, AppInfo.IsPackaged);
        Dispatcher.UIThread.UnhandledException += (_, e) => logger.LogCritical(e.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => logger.LogCritical(e.ExceptionObject as Exception, "Unhandled AppDomain exception");
        TaskScheduler.UnobservedTaskException += (_, e) => logger.LogCritical(e.Exception, "Unobserved task exception");
        Avalonia.Logging.Logger.Sink = new AvaloniaLogSink(_host.Services.GetRequiredService<ILogger<AvaloniaLogSink>>());

        var settings = _host.Services.GetRequiredService<ShellSettings>();
        logger.LogInformation("Shell settings: stub defaults (backdrop {Backdrop}, tray icon {Tray})", settings.Backdrop, settings.ShowTrayIcon);
        RefreshRunAtLogin(settings, logger);

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.CreateTrayIcon();
        instance.ListenForShow(() =>
        {
            logger.LogInformation("Show signal from a second launch");
            window.ShowFromTray();
        });

        var args = desktop.Args ?? [];
        var tray = StartupRegistration.HasArgument(args, StartupRegistration.TrayArgument) || settings.LaunchToTray;
        var noActivate = StartupRegistration.HasArgument(args, StartupRegistration.NoActivateArgument);
        if (tray || noActivate)
        {
            window.ForegroundGate.Hold();
        }

        if (tray && settings.ShowTrayIcon)
        {
            window.HideToTray();
            logger.LogInformation("Main window started hidden in tray");
        }
        else if (tray || noActivate)
        {
            window.ShowWithoutFocus();
            logger.LogInformation("Main window shown without focus");
        }
        else
        {
            window.ShowFromTray();
            logger.LogInformation("Main window activated");
        }

        new UiWatchdog(window, _host.Services.GetRequiredService<ILogger<UiWatchdog>>()).Start();
    }

    private static void RefreshRunAtLogin(ShellSettings settings, ILogger logger)
    {
        if (!settings.LaunchOnStartup)
        {
            return;
        }

        try
        {
            logger.LogInformation("Run at login refreshed: {Command}", StartupRegistration.Register(settings.LaunchToTray));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            logger.LogError(ex, "Refreshing the run-at-login value failed");
        }
    }
}
