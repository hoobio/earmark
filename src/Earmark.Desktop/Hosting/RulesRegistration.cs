using Earmark.Desktop.ViewModels.Rules;
using Earmark.Desktop.Views;

using Microsoft.Extensions.DependencyInjection;

namespace Earmark.Desktop.Hosting;

internal static class RulesRegistration
{
    public static IServiceCollection AddRulesArea(this IServiceCollection services) =>
        services
            .AddSingleton<RulesViewModel>()
            .AddSingleton<RulesPage>()
            .AddKeyedSingleton<Action<Guid>>("OpenRule", (provider, _) => id =>
            {
                provider.GetRequiredService<MainWindow>().Navigate("Rules");
                provider.GetRequiredService<RulesViewModel>().RequestFocus(id);
            });
}
