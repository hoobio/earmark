using Earmark.Audio;
using Earmark.Audio.Services;
using Earmark.Core;
using Earmark.Core.Logging;
using Earmark.Core.Services;
using Earmark.Core.Settings;
using Earmark.Desktop.Services;
using Earmark.Desktop.Views;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Earmark.Desktop.Hosting;

internal static class ServiceRegistration
{
    public static string DataDirectory { get; } = ChannelPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hoobi", "Earmark"));

    public static string LogDirectory { get; } = Path.Combine(ChannelPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Earmark")), "logs");

    public static string CurrentLogPath { get; } = Path.Combine(LogDirectory, $"earmark-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    public static HostApplicationBuilder AddEarmark(this HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var fileLogger = new FileLoggerProvider(CurrentLogPath);
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.AddProvider(fileLogger);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Services.AddSingleton(fileLogger);

        builder.Services.AddEarmarkCore(Path.Combine(DataDirectory, "rules.json"), Path.Combine(DataDirectory, "settings.json"));
        builder.Services.AddEarmarkInterop();
        builder.Services.AddUpdates();

        builder.Services.AddSingleton<MainWindow>();
        builder.Services
            .AddDevicesArea()
            .AddRulesArea()
            .AddSessionsArea()
            .AddSettingsArea()
            .AddQuickControlsArea();

        return builder;
    }

    public static IServiceCollection AddUpdates(this IServiceCollection services) => services.AddSingleton(sp => new UpdateService(
        sp.GetRequiredService<SettingsService>(),
        PackageIdentity.IsPackaged,
        PackageIdentity.IsPackaged && AppInfo.IsRelease ? new StoreUpdates(sp, sp.GetRequiredService<ILogger<StoreUpdates>>()) : null,
        sp.GetRequiredService<ILogger<UpdateService>>()));

    private static string ChannelPath(string baseDir) => AppInfo.ChannelFolder is { Length: > 0 } channel ? Path.Combine(baseDir, channel) : baseDir;
}
