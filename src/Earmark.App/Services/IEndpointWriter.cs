using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.WaveLink;

using Microsoft.Extensions.Logging;

namespace Earmark.App.Services;

/// <summary>
/// Single entry point for "set state on this endpoint" that picks the right transport. Wave
/// Link virtual capture endpoints ignore AudioEndpointVolume.Mute / .MasterVolumeLevelScalar
/// (the properties flip but WL reads upstream and keeps its own values), so the writer
/// resolves the matching WL input and routes through Wave Link's setInputConfig when there's
/// a hit; everything else (and the fallback path when WL refuses) takes the standard Windows
/// endpoint route. <see cref="DeviceCard"/> (user toggles / slider) and the rule engine
/// both call through here so the WL-vs-Windows decision lives in one place.
/// </summary>
public interface IEndpointWriter
{
    Task<bool> SetMutedAsync(AudioEndpoint endpoint, bool muted, CancellationToken ct = default);
    Task<bool> SetVolumeAsync(AudioEndpoint endpoint, float level, CancellationToken ct = default);
}

internal sealed class EndpointWriter : IEndpointWriter
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

        // For Wave Link capture endpoints the WL transport is the source of truth - the
        // Windows endpoint mute on a WL virtual capture device is metadata only, and on a
        // hardware mic that's wired into WL the Windows mute doesn't reach the WL pipeline
        // (apps reading via WL's virtual mics keep hearing). Two routing paths:
        //   - WL InputDevice: id matches the Windows MMDevice id verbatim (hardware mics)
        //   - WL Mix: name matches the user-facing endpoint name (virtual mix outputs)
        // Fall back to the Windows endpoint only when WL has no match or the WS call fails.
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

        // Run the Windows-endpoint COM off the UI thread (this is reachable synchronously from
        // the slider's OnVolumeChanged); a COM call on the dispatcher can cross-apartment-
        // deadlock against the MTA audio worker threads.
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

        // Off the UI thread for the same reason as SetMutedAsync above.
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
        // WL's setInputDevice doesn't expose a 0-1 level - the channel uses a "gain" knob
        // with a per-device lookup table. Falling through to the Windows endpoint volume is
        // the honest answer here (and it usually matches what the hardware preamp wants).
        public override Task<bool> LevelAsync(IWaveLinkService waveLink, float level, CancellationToken ct) =>
            Task.FromResult(false);
    }

    private WaveLinkRoute? ResolveWaveLinkRoute(AudioEndpoint endpoint)
    {
        // If WL isn't currently reachable (off in settings, shut down externally,
        // mid-reconnect), skip the WL transport so the writer falls through to the Windows
        // endpoint - otherwise a stale LastSnapshot would point us at a service we can't
        // actually talk to.
        if (!_waveLink.IsAvailable) return null;
        if (endpoint.Flow != EndpointFlow.Capture) return null;

        var snapshot = _waveLink.LastSnapshot;
        if (snapshot is null) return null;

        // Hardware-input match first: WL exposes the same MMDevice id Windows does, so the
        // capture endpoint id matches the WL device id exactly. The single-input case is
        // the common one; for multi-input interfaces (SSL 2's Input 1 / Input 2 / merged)
        // the inner input ids also match the Windows endpoint id for each channel.
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

        // Virtual capture endpoint -> WL Mix by name. Strip the trailing "(driver)" so
        // "Microphone Mix (Elgato Virtual Audio)" matches "Microphone Mix".
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
