using Earmark.Audio.Routing;
using Earmark.Audio.Services;
using Earmark.Audio.WaveLink;
using Earmark.Core.Audio;
using Earmark.Core.Services;
using Earmark.Core.WaveLink;

using Microsoft.Extensions.DependencyInjection;

namespace Earmark.Audio;

public static class AudioServiceCollectionExtensions
{
    public static IServiceCollection AddEarmarkInterop(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAudioEndpointService, AudioEndpointService>();
        services.AddSingleton<AudioSessionService>();
        services.AddSingleton<IAudioSessionService>(sp => sp.GetRequiredService<AudioSessionService>());
        services.AddSingleton<IAudioSessionMeterService>(sp => sp.GetRequiredService<AudioSessionService>());
        services.AddSingleton<IAudioPolicyService, AudioPolicyService>();
        services.AddSingleton<INowPlayingService, NowPlayingService>();
        services.AddSingleton<IRunningProcessProvider, RunningProcessProvider>();
        services.AddSingleton<IWaveLinkService, WaveLinkService>();
        services.AddSingleton<IBluetoothAudioControl, BluetoothAudioControl>();
        services.AddSingleton<EndpointWriter>();
        services.AddSingleton<RoutingApplier>();
        services.AddSingleton<ProcessControlService>();
        services.AddSingleton<StartupSettingsApplier>();
        return services;
    }
}
