using Earmark.Core.Persistence;
using Earmark.Core.Routing;
using Earmark.Core.Services;

using Microsoft.Extensions.DependencyInjection;

namespace Earmark.Core;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddEarmarkCore(this IServiceCollection services, string? rulesPath = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IRuleStore>(_ => new JsonRuleStore(rulesPath ?? JsonRuleStore.DefaultPath));
        services.AddSingleton<IRulesService, RulesService>();
        services.AddSingleton<IRuleMatcher, RuleMatcher>();
        services.AddSingleton<IRuleEvaluator, RuleEvaluator>();
        return services;
    }
}
