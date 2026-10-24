using Earmark.Desktop.Views;

using Microsoft.Extensions.DependencyInjection;

namespace Earmark.Desktop.Hosting;

internal static class QuickControlsRegistration
{
    public static IServiceCollection AddQuickControlsArea(this IServiceCollection services) =>
        services.AddSingleton<QuickControlsWindow>();
}
