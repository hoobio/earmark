using Earmark.Desktop.Services;
using Earmark.Desktop.ViewModels;
using Earmark.Desktop.Views;

using Microsoft.Extensions.DependencyInjection;

namespace Earmark.Desktop.Hosting;

internal static class SettingsRegistration
{
    public static IServiceCollection AddSettingsArea(this IServiceCollection services) => services
        .AddSingleton<SettingsViewModel>()
        .AddSingleton<AboutViewModel>()
        .AddSingleton<SettingsPage>()
        .AddSingleton<NotificationService>()
        .AddKeyedSingleton<Action<string>>("OpenSettingsSection", (provider, _) => key =>
        {
            provider.GetRequiredService<MainWindow>().Navigate("Settings");
            provider.GetRequiredService<SettingsPage>().RevealSection(key);
        });
}
