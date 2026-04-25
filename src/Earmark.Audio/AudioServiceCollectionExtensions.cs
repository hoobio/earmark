using Earmark.Audio.Services;
using Earmark.Core.Audio;

using Microsoft.Extensions.DependencyInjection;

namespace Earmark.Audio;

public static class AudioServiceCollectionExtensions
{
    public static IServiceCollection AddEarmarkInterop(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAudioEndpointService, AudioEndpointService>();
        services.AddSingleton<IAudioSessionService, AudioSessionService>();
        services.AddSingleton<IAudioPolicyService, AudioPolicyService>();
        return services;
    }
}
