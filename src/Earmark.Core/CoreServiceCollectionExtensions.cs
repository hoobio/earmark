using Earmark.Core.Persistence;
using Earmark.Core.Services;
using Earmark.Core.Settings;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Earmark.Core;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddEarmarkCore(this IServiceCollection services, string rulesPath, string settingsPath)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IRuleStore>(sp => new JsonRuleStore(rulesPath, sp.GetService<ILogger<JsonRuleStore>>()));
        services.AddSingleton<IRulesService, RulesService>();
        services.AddSingleton(sp => new SettingsService(settingsPath, sp.GetService<ILogger<SettingsService>>()));
        services.AddSingleton<DeviceDefaultsService>();
        return services;
    }
}
