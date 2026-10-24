using Avalonia.Controls;

using Earmark.Desktop.Services;
using Earmark.Desktop.Views;

using Microsoft.Extensions.DependencyInjection;

namespace Earmark.Desktop.Hosting;

internal static class QuickControlsRegistration
{
    public static IServiceCollection AddQuickControlsArea(this IServiceCollection services) =>
        services
            .AddSingleton<QuickControlsVisibility>()
            .AddSingleton<QuickControlsWindow>()
            .AddSingleton<QuickControlsSettings>()
            .AddKeyedSingleton<Control>("QuickControlsSettings", (provider, _) => provider.GetRequiredService<QuickControlsSettings>());
}
