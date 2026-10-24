using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using Earmark.Audio.Routing;
using Earmark.Audio.Services;
using Earmark.Core;
using Earmark.Core.Audio;
using Earmark.Core.Services;
using Earmark.Core.Settings;
using Earmark.Desktop.Hosting;
using Earmark.Desktop.Services;
using Earmark.Desktop.Styles;
using Earmark.Desktop.Views;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using StartupArgs = Earmark.Desktop.Services.StartupRegistration;

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

    public override async void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || Instance is not { } instance)
        {
            return;
        }

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _host = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true }).AddEarmark().Build();
        var services = _host.Services;
        var logger = services.GetRequiredService<ILogger<App>>();
        desktop.Exit += (_, _) =>
        {
            logger.LogInformation("Shutting down host");
            _host.Dispose();
        };

        logger.LogInformation("Earmark starting; log file: {LogPath}", ServiceRegistration.CurrentLogPath);
        logger.LogInformation("Earmark {Version} ({Commit}) on {Os}, {Arch}, packaged {Packaged}", AppInfo.DisplayVersion, AppInfo.ShortCommit ?? "no commit", RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture, PackageIdentity.IsPackaged);
        Dispatcher.UIThread.UnhandledException += (_, e) => logger.LogCritical(e.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => logger.LogCritical(e.ExceptionObject as Exception, "Unhandled AppDomain exception");
        TaskScheduler.UnobservedTaskException += (_, e) => logger.LogCritical(e.Exception, "Unobserved task exception");
        Avalonia.Logging.Logger.Sink = new AvaloniaLogSink(services.GetRequiredService<ILogger<AvaloniaLogSink>>());

        try
        {
            await Task.Run(() => LoadEngineAsync(services, logger));
            ShowShell(desktop, instance, services, logger);
            _ = Task.Run(() => StartRoutingAsync(services, logger)).ContinueWith(t => logger.LogError(t.Exception, "Background startup failed"), TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Startup failed");
            throw;
        }
    }

    private static async Task LoadEngineAsync(IServiceProvider services, ILogger logger)
    {
        var settings = services.GetRequiredService<SettingsService>();
        try
        {
            await settings.LoadAsync();
            logger.LogInformation("Settings loaded (backdrop {Backdrop}, tray icon {Tray})", settings.Current.Backdrop, settings.Current.ShowTrayIcon);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Settings failed to load; using defaults with saves blocked (copy at {Copy})", settings.CorruptCopyPath ?? "none");
        }

        services.GetRequiredService<StartupSettingsApplier>().Start();
        services.GetRequiredService<NotificationService>().Register();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ = services.GetRequiredService<IAudioEndpointService>();
        var endpointsMs = sw.ElapsedMilliseconds;
        _ = services.GetRequiredService<IAudioSessionService>();
        var sessionsMs = sw.ElapsedMilliseconds - endpointsMs;
        _ = services.GetRequiredService<IAudioSessionMeterService>();
        logger.LogInformation("Audio services initialized in {TotalMs} ms (endpoints {EpMs}, sessions {SessMs}, meters {MeterMs})", sw.ElapsedMilliseconds, endpointsMs, sessionsMs, sw.ElapsedMilliseconds - endpointsMs - sessionsMs);

        var rules = services.GetRequiredService<IRulesService>();
        try
        {
            await rules.LoadAsync();
            logger.LogInformation("Rules loaded ({Count} rules, saves blocked {Blocked})", rules.Rules.Count, rules.IsSaveBlocked);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rules failed to load; starting with no rules and saves blocked (copy at {Copy})", rules.CorruptCopyPath ?? "none");
        }
    }

    private static async Task StartRoutingAsync(IServiceProvider services, ILogger logger)
    {
        try
        {
            if (services.GetRequiredService<IRulesService>().IsSaveBlocked || services.GetRequiredService<SettingsService>().IsSaveBlocked)
            {
                logger.LogWarning("Skipping new-install seeding while rule or settings saves are blocked");
            }
            else
            {
                await services.GetRequiredService<DeviceDefaultsService>().SeedDefaultsIfEmptyAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Seeding new-install defaults failed");
        }
        finally
        {
            services.GetRequiredService<RoutingApplier>().Start();
            logger.LogInformation("Routing applier started");
        }
    }

    private static void ShowShell(IClassicDesktopStyleApplicationLifetime desktop, SingleInstance instance, IServiceProvider services, ILogger logger)
    {
        var settings = services.GetRequiredService<SettingsService>().Current;
        var window = services.GetRequiredService<MainWindow>();
        window.CreateTrayIcon();
        services.GetRequiredService<TaskbarMediaControls>().Attach(window);
        services.GetRequiredService<QuickControlsWindow>().Start();
        window.ReapplyRulesRequested += (_, _) => _ = services.GetRequiredService<RoutingApplier>().ApplyAllAsync(force: true)
            .ContinueWith(t => logger.LogError(t.Exception, "Reapply from the tray failed"), TaskContinuationOptions.OnlyOnFaulted);
        instance.ListenForShow(() =>
        {
            logger.LogInformation("Show signal from a second launch");
            window.ShowFromTray();
        });

        var args = desktop.Args ?? [];
        var openPage = StartupArgs.GetValue(args, StartupArgs.OpenArgumentPrefix);
        var rightmost = string.Equals(StartupArgs.GetValue(args, StartupArgs.MonitorArgumentPrefix), "rightmost", StringComparison.OrdinalIgnoreCase)
            ? window.Screens.All.MaxBy(screen => screen.Bounds.X)
            : null;
        if (string.Equals(openPage, StartupArgs.QuickControlsPage, StringComparison.OrdinalIgnoreCase))
        {
            // Devices load asynchronously, so an immediate open would show only the placeholder panel.
            DispatcherTimer.RunOnce(() => services.GetRequiredService<QuickControlsWindow>().OpenWithoutFocus(rightmost), TimeSpan.FromSeconds(3));
            logger.LogInformation("Opening Quick Controls from the command line");
        }
        else if (openPage is not null)
        {
            window.Navigate(openPage);
            logger.LogInformation("Opened page {Page} from the command line", openPage);
        }

        if (rightmost is { } screen)
        {
            var area = screen.WorkingArea;
            var size = PixelSize.FromSize(new Size(window.Width, window.Height), screen.Scaling);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(area.X + Math.Max(0, (area.Width - size.Width) / 2), area.Y + Math.Max(0, (area.Height - size.Height) / 2));
            logger.LogInformation("Main window placed on the rightmost screen at {Area}", area);
        }

        var tray = openPage is null && (StartupArgs.HasArgument(args, StartupArgs.TrayArgument) || settings.LaunchToTray);
        var noActivate = StartupArgs.HasArgument(args, StartupArgs.NoActivateArgument);
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

        new UiWatchdog(window, services.GetRequiredService<ILogger<UiWatchdog>>()).Start();
        _ = OfferUnblockIfBlockedAsync(window, services.GetRequiredService<SettingsService>(), services.GetRequiredService<IRulesService>(), logger)
            .ContinueWith(t => logger.LogError(t.Exception, "Blocked-save notice failed"), TaskContinuationOptions.OnlyOnFaulted);
        Dispatcher.UIThread.Post(() => WireUpdatePill(window, services.GetRequiredService<UpdateService>(), logger), DispatcherPriority.Background);
    }

    private static Task OfferUnblockIfBlockedAsync(MainWindow window, SettingsService settings, IRulesService rules, ILogger logger) => Task.WhenAll(
        OfferUnblockAsync(window, logger, settings.IsSaveBlocked, settings.IsCorrupt, "settings.json", "Settings", settings.CorruptCopyPath, "default settings", settings.UnblockSaves),
        OfferUnblockAsync(window, logger, rules.IsSaveBlocked, rules.IsCorrupt, "rules.json", "Rules", rules.CorruptCopyPath, "an empty rule list", rules.UnblockSaves));

    private static async Task OfferUnblockAsync(MainWindow window, ILogger logger, bool blocked, bool corrupt, string file, string what, string? copyPath, string fresh, Action unblock)
    {
        if (!blocked)
        {
            return;
        }

        logger.LogWarning("{What} saves are blocked: {File} failed to load ({Reason}, copy at {Copy}); showing the notice", what, file, corrupt ? "corrupt" : "unreadable", copyPath ?? "none");
        var copy = copyPath is { } path ? $"A copy of the unreadable file is saved at {path}." : "No copy of the file could be saved.";
        var message = corrupt
            ? $"Earmark could not read {file}, so {what.ToLowerInvariant()} changes are not saved. {copy} Allow saves to start again with {fresh}, or keep saves blocked and repair the file."
            : $"Earmark could not open {file}, so {what.ToLowerInvariant()} changes are not saved until Earmark restarts. The file is unchanged. Another program, such as OneDrive, can hold it open.";
        if (await window.ShowNoticeAsync(message, corrupt ? "Allow saves" : null, corrupt ? "Keep blocked" : "Dismiss"))
        {
            unblock();
            logger.LogWarning("{What} saves unblocked by the user", what);
        }
    }

    private static void WireUpdatePill(MainWindow window, UpdateService updates, ILogger logger)
    {
        var pill = window.UpdateButton;
        void Refresh()
        {
            pill.IsVisible = updates.Status == UpdateStatus.UpdateAvailable;
            ToolTip.SetTip(pill, updates.IsStore ? "An update is available in the Microsoft Store" : updates.LatestVersion is { } latest ? $"{latest} is available" : null);
        }

        updates.StatusChanged += (_, _) => Ui.Post(Refresh);
        pill.Click += async (_, _) =>
        {
            if (updates.IsStore)
            {
                await updates.InstallStoreUpdateAsync();
            }
            else if (updates.LatestReleaseUrl is { } url && !await window.Launcher.LaunchUriAsync(new Uri(url)))
            {
                logger.LogWarning("Could not open {Url}", url);
            }
        };
        Refresh();
        _ = updates.CheckForUpdatesAsync(manual: false);
    }
}
