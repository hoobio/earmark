using Earmark.Core.Persistence;
using Earmark.Core.Routing;
using Earmark.Core.Services;
using Earmark.Core.Settings;

using Microsoft.Extensions.DependencyInjection;

namespace Earmark.Core;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddEarmarkCore(this IServiceCollection services, string rulesPath, string settingsPath)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IRuleStore>(_ => new JsonRuleStore(rulesPath));
        services.AddSingleton<IRulesService, RulesService>();
        services.AddSingleton<IRuleMatcher, RuleMatcher>();
        services.AddSingleton<IRuleEvaluator, RuleEvaluator>();
        services.AddSingleton(_ => new SettingsService(settingsPath));
        services.AddSingleton<DeviceDefaultsService>();
        return services;
    }
}
