using Earmark.Desktop.Logging;
using Earmark.Desktop.Services;
using Earmark.Desktop.Views;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Earmark.Desktop.Hosting;

internal static class ServiceRegistration
{
    public static HostApplicationBuilder AddEarmark(this HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var fileLogger = new FileLoggerProvider(AppInfo.CurrentLogPath);
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.AddProvider(fileLogger);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Services.AddSingleton(fileLogger);

        builder.Services.AddSingleton(new ShellSettings());

        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<QuickControlsWindow>();
        builder.Services.AddSingleton<DevicesPage>();
        builder.Services.AddSingleton<RulesPage>();
        builder.Services.AddSingleton<SessionsPage>();
        builder.Services.AddSingleton<SettingsPage>();

        return builder;
    }
}
