using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.WaveLink;

using Microsoft.Extensions.Logging;

namespace Earmark.Audio.Routing;

public sealed class EndpointWriter
{
    private readonly IAudioEndpointService _endpoints;
    private readonly IWaveLinkService _waveLink;
    private readonly ILogger<EndpointWriter> _logger;

    public EndpointWriter(
        IAudioEndpointService endpoints,
        IWaveLinkService waveLink,
        ILogger<EndpointWriter> logger)
    {
        _endpoints = endpoints;
        _waveLink = waveLink;
        _logger = logger;
    }

    public async Task<bool> SetMutedAsync(AudioEndpoint endpoint, bool muted, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        // Windows mute/volume on a Wave Link capture endpoint is metadata only (WL reads upstream and keeps its own values), so WL is written first.
        var route = ResolveWaveLinkRoute(endpoint);
        if (route is not null)
        {
            try
            {
                var ok = await route.MuteAsync(_waveLink, muted, ct).ConfigureAwait(false);
                if (ok) return true;
                _logger.LogDebug("EndpointWriter: WL refused mute for {Device}; falling back", endpoint.DisplayName);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EndpointWriter: WL mute write threw for {Device}; falling back", endpoint.DisplayName);
            }
        }

        // A COM call on the dispatcher thread can cross-apartment deadlock against the MTA audio workers (reachable from the slider's OnVolumeChanged).
        return await Task.Run(() => _endpoints.SetMuted(endpoint.Id, muted)).ConfigureAwait(false);
    }

    public async Task<bool> SetVolumeAsync(AudioEndpoint endpoint, float level, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var route = ResolveWaveLinkRoute(endpoint);
        if (route is not null)
        {
            try
            {
                var ok = await route.LevelAsync(_waveLink, level, ct).ConfigureAwait(false);
                if (ok) return true;
                _logger.LogDebug("EndpointWriter: WL refused volume for {Device}; falling back", endpoint.DisplayName);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EndpointWriter: WL volume write threw for {Device}; falling back", endpoint.DisplayName);
            }
        }

        return await Task.Run(() => _endpoints.SetVolume(endpoint.Id, level)).ConfigureAwait(false);
    }

    private abstract record WaveLinkRoute
    {
        public abstract Task<bool> MuteAsync(IWaveLinkService waveLink, bool muted, CancellationToken ct);
        public abstract Task<bool> LevelAsync(IWaveLinkService waveLink, float level, CancellationToken ct);
    }

    private sealed record MixRoute(string MixId) : WaveLinkRoute
    {
        public override Task<bool> MuteAsync(IWaveLinkService waveLink, bool muted, CancellationToken ct) =>
            waveLink.SetMixMutedAsync(MixId, muted, ct);
        public override Task<bool> LevelAsync(IWaveLinkService waveLink, float level, CancellationToken ct) =>
            waveLink.SetMixLevelAsync(MixId, level, ct);
    }

    private sealed record InputDeviceRoute(string DeviceId, string InputId) : WaveLinkRoute
    {
        public override Task<bool> MuteAsync(IWaveLinkService waveLink, bool muted, CancellationToken ct) =>
            waveLink.SetInputDeviceMutedAsync(DeviceId, InputId, muted, ct);
        // WL's setInputDevice has no 0-1 level (only a per-device "gain" lookup), so volume falls through to the Windows endpoint.
        public override Task<bool> LevelAsync(IWaveLinkService waveLink, float level, CancellationToken ct) =>
            Task.FromResult(false);
    }

    private WaveLinkRoute? ResolveWaveLinkRoute(AudioEndpoint endpoint)
    {
        // A stale LastSnapshot survives WL going away, so availability is checked first.
        if (!_waveLink.IsAvailable) return null;
        if (endpoint.Flow != EndpointFlow.Capture) return null;

        var snapshot = _waveLink.LastSnapshot;
        if (snapshot is null) return null;

        // WL exposes the same MMDevice id Windows does, for the device and for each inner input of multi-input interfaces.
        foreach (var device in snapshot.InputDevices)
        {
            if (!string.Equals(device.DeviceId, endpoint.Id, StringComparison.OrdinalIgnoreCase)) continue;
            WaveLinkInputChannelInfo? input = null;
            foreach (var candidate in device.Inputs)
            {
                if (string.Equals(candidate.InputId, endpoint.Id, StringComparison.OrdinalIgnoreCase))
                {
                    input = candidate;
                    break;
                }
            }
            if (input is null && device.Inputs.Count > 0) input = device.Inputs[0];
            if (input is null) continue;
            return new InputDeviceRoute(device.DeviceId, input.InputId);
        }

        // Virtual mix endpoints are named "Microphone Mix (Elgato Virtual Audio)" but the WL mix is "Microphone Mix".
        var bare = StripTrailingParenthetical(endpoint.FriendlyName);
        foreach (var mix in snapshot.Mixes)
        {
            if (string.Equals(mix.Name, bare, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mix.Name, endpoint.FriendlyName, StringComparison.OrdinalIgnoreCase))
            {
                return new MixRoute(mix.Id);
            }
        }
        return null;
    }

    private static string StripTrailingParenthetical(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var open = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (open <= 0 || !name.EndsWith(')')) return name;
        return name.Substring(0, open);
    }
}
