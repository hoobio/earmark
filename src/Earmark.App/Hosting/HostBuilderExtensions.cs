using Earmark.App.Logging;
using Earmark.App.Services;
using Earmark.App.Settings;
using Earmark.App.ViewModels;
using Earmark.App.Views;
using Earmark.Audio;
using Earmark.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Earmark.App.Hosting;

internal static class HostBuilderExtensions
{
    /// <summary>Roaming data root for persisted user content (rules, settings). Per build channel
    /// so Dev / Prerelease / Release installs never share or clobber each other's data. In
    /// %APPDATA% (not the OneDrive-backed Documents folder) so the startup read never stalls on a
    /// cloud-syncing file. A stable Release build sits at the base; non-stable channels nest under it.</summary>
    public static string DataDirectory { get; } = ChannelPath(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Hoobi",
            "Earmark"));

    /// <summary>Cache root (logs) in %LOCALAPPDATA% - machine-local churn that must not roam or
    /// sync. Channel-segregated to match the data root.</summary>
    public static string LogDirectory { get; } = Path.Combine(
        ChannelPath(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Earmark")),
        "logs");

    public static string CurrentLogPath { get; } = Path.Combine(
        LogDirectory,
        $"earmark-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    private static string ChannelPath(string baseDir)
    {
        var channel = AppInfo.ChannelFolder;
        return string.IsNullOrEmpty(channel) ? baseDir : Path.Combine(baseDir, channel);
    }

    public static HostApplicationBuilder ConfigureEarmark(this HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var fileLoggerProvider = new FileLoggerProvider(CurrentLogPath);
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.AddProvider(fileLoggerProvider);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        builder.Services.AddSingleton(fileLoggerProvider);

        builder.Services.AddEarmarkCore(Path.Combine(DataDirectory, "rules.json"));
        builder.Services.AddEarmarkInterop();

        builder.Services.AddSingleton<ISettingsService>(
            _ => new SettingsService(Path.Combine(DataDirectory, "settings.json")));
        builder.Services.AddSingleton<IRoutingApplier, RoutingApplier>();
        builder.Services.AddSingleton<IDispatcherQueueProvider, DispatcherQueueProvider>();
        builder.Services.AddSingleton<INavigationService, NavigationService>();
        builder.Services.AddSingleton<IWindowChromeManager, WindowChromeManager>();
        builder.Services.AddSingleton<INotificationService, NotificationService>();
        builder.Services.AddSingleton<IInAppNotificationService, InAppNotificationService>();
        builder.Services.AddSingleton<IProcessControlService, ProcessControlService>();
        builder.Services.AddSingleton<IUpdateService, UpdateService>();
        builder.Services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();
        builder.Services.AddSingleton<IQuickControlsService, QuickControlsService>();
        builder.Services.AddSingleton<IEndpointWriter, EndpointWriter>();
        builder.Services.AddSingleton<ISessionIconService, SessionIconService>();
        builder.Services.AddSingleton<IWaveLinkNameReconciler, WaveLinkNameReconciler>();
        builder.Services.AddSingleton<IWaveLinkVisualService, WaveLinkVisualService>();
        builder.Services.AddSingleton<INowPlayingArtworkService, NowPlayingArtworkService>();
        builder.Services.AddSingleton<IDeviceDefaultsService, DeviceDefaultsService>();
        builder.Services.AddSingleton<StartupSettingsApplier>();

        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<HomeViewModel>();
        builder.Services.AddSingleton<RulesViewModel>();
        builder.Services.AddSingleton<SessionsViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<AboutViewModel>();

        builder.Services.AddSingleton<HomePage>();
        builder.Services.AddSingleton<RulesPage>();
        builder.Services.AddSingleton<SessionsPage>();
        builder.Services.AddSingleton<SettingsPage>();

        return builder;
    }
}
