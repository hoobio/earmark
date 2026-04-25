using Earmark.App.Services;
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
    public static HostApplicationBuilder ConfigureEarmark(this HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services.AddEarmarkCore();
        builder.Services.AddEarmarkInterop();

        builder.Services.AddSingleton<IRoutingApplier, RoutingApplier>();
        builder.Services.AddSingleton<IDispatcherQueueProvider, DispatcherQueueProvider>();
        builder.Services.AddSingleton<INavigationService, NavigationService>();

        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddTransient<RulesViewModel>();
        builder.Services.AddTransient<SessionsViewModel>();
        builder.Services.AddTransient<DevicesViewModel>();
        builder.Services.AddTransient<RuleEditorViewModel>();

        builder.Services.AddTransient<RulesPage>();
        builder.Services.AddTransient<SessionsPage>();
        builder.Services.AddTransient<DevicesPage>();

        return builder;
    }
}
