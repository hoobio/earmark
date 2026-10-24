using Earmark.Desktop.Views;

using Microsoft.Extensions.DependencyInjection;

namespace Earmark.Desktop.Hosting;

internal static class RulesRegistration
{
    public static IServiceCollection AddRulesArea(this IServiceCollection services) =>
        services.AddSingleton<RulesPage>();
}
