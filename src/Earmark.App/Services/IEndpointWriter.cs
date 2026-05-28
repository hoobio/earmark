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

        // If Wave Link has this device wired up as an input channel (virtual or physical
        // routed through it), the WS path is the source of truth: WL mirrors the new state
        // back to the Windows endpoint so direct WASAPI consumers see it without us
        // double-writing. Only fall back to the Windows endpoint when WL has no match or
        // the WS call itself fails.
        var waveLinkInputId = ResolveWaveLinkInputIdentifier(endpoint);
        if (waveLinkInputId is not null)
        {
            try
            {
                var ok = await _waveLink.SetInputMuteAsync(waveLinkInputId, muted, ct).ConfigureAwait(false);
                if (ok) return true;
                _logger.LogDebug("EndpointWriter: WL setInputConfig refused mute for {Device}; falling back", endpoint.DisplayName);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EndpointWriter: WL mute write threw for {Device}; falling back", endpoint.DisplayName);
            }
        }

        return _endpoints.SetMuted(endpoint.Id, muted);
    }

    public async Task<bool> SetVolumeAsync(AudioEndpoint endpoint, float level, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var waveLinkInputId = ResolveWaveLinkInputIdentifier(endpoint);
        if (waveLinkInputId is not null)
        {
            try
            {
                var ok = await _waveLink.SetInputVolumeAsync(waveLinkInputId, level, ct).ConfigureAwait(false);
                if (ok) return true;
                _logger.LogDebug("EndpointWriter: WL setInputConfig refused volume for {Device}; falling back", endpoint.DisplayName);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EndpointWriter: WL volume write threw for {Device}; falling back", endpoint.DisplayName);
            }
        }

        return _endpoints.SetVolume(endpoint.Id, level);
    }

    private string? ResolveWaveLinkInputIdentifier(AudioEndpoint endpoint)
    {
        // If WL isn't currently reachable (turned off in settings, shut down externally,
        // mid-reconnect), skip the WL transport so the writer falls straight through to the
        // Windows endpoint - otherwise a stale LastSnapshot would route us at a service we
        // can't actually talk to.
        if (!_waveLink.IsAvailable) return null;
        if (endpoint.Flow != EndpointFlow.Capture) return null;

        var snapshot = _waveLink.LastSnapshot;
        if (snapshot is null || snapshot.Inputs.Count == 0) return null;

        var bare = StripTrailingParenthetical(endpoint.FriendlyName);
        foreach (var input in snapshot.Inputs)
        {
            if (string.Equals(input.Name, bare, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(input.Name, endpoint.FriendlyName, StringComparison.OrdinalIgnoreCase))
            {
                return input.Identifier;
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
