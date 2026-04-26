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
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Earmark",
        "logs");

    public static string CurrentLogPath { get; } = Path.Combine(
        LogDirectory,
        $"earmark-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    public static HostApplicationBuilder ConfigureEarmark(this HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var fileLoggerProvider = new FileLoggerProvider(CurrentLogPath);
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.AddProvider(fileLoggerProvider);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        builder.Services.AddSingleton(fileLoggerProvider);

        builder.Services.AddEarmarkCore();
        builder.Services.AddEarmarkInterop();

        builder.Services.AddSingleton<ISettingsService, SettingsService>();
        builder.Services.AddSingleton<IRoutingApplier, RoutingApplier>();
        builder.Services.AddSingleton<IDispatcherQueueProvider, DispatcherQueueProvider>();
        builder.Services.AddSingleton<INavigationService, NavigationService>();
        builder.Services.AddSingleton<IWindowChromeManager, WindowChromeManager>();
        builder.Services.AddSingleton<StartupSettingsApplier>();

        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddTransient<RulesViewModel>();
        builder.Services.AddTransient<SessionsViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();

        builder.Services.AddTransient<RulesPage>();
        builder.Services.AddTransient<SessionsPage>();
        builder.Services.AddTransient<SettingsPage>();

        return builder;
    }
}
