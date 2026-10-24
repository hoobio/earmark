using Earmark.Desktop.Views;

using Microsoft.Extensions.DependencyInjection;

namespace Earmark.Desktop.Hosting;

internal static class SettingsRegistration
{
    public static IServiceCollection AddSettingsArea(this IServiceCollection services) =>
        services.AddSingleton<SettingsPage>();
}
