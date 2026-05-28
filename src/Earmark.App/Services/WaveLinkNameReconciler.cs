using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.WaveLink;

using Microsoft.Extensions.Logging;

namespace Earmark.App.Services;

public sealed record WaveLinkReconcileResult(
    int Inspected,
    int Renamed,
    int Skipped,
    int Unmatched,
    string? Error);

public interface IWaveLinkNameReconciler
{
    /// <summary>
    /// Walks the current Wave Link snapshot and renames any Windows endpoints whose FriendlyName
    /// differs from the Wave Link device label. Match-by-id first, fall back to match-by-name
    /// because Wave Link's "id" field is not always the MMDevice id across releases.
    /// </summary>
    Task<WaveLinkReconcileResult> ReconcileAsync(CancellationToken ct = default);
}

internal sealed class WaveLinkNameReconciler : IWaveLinkNameReconciler
{
    private readonly IWaveLinkService _waveLink;
    private readonly IAudioEndpointService _endpoints;
    private readonly ILogger<WaveLinkNameReconciler> _logger;

    public WaveLinkNameReconciler(
        IWaveLinkService waveLink,
        IAudioEndpointService endpoints,
        ILogger<WaveLinkNameReconciler> logger)
    {
        _waveLink = waveLink;
        _endpoints = endpoints;
        _logger = logger;
    }

    public async Task<WaveLinkReconcileResult> ReconcileAsync(CancellationToken ct = default)
    {
        if (!_waveLink.IsEnabled)
        {
            return new WaveLinkReconcileResult(0, 0, 0, 0, "Wave Link integration is off");
        }

        WaveLinkSnapshot? snapshot;
        try
        {
            snapshot = await _waveLink.GetSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reconcile: snapshot fetch failed");
            return new WaveLinkReconcileResult(0, 0, 0, 0, "Wave Link snapshot fetch failed");
        }

        if (snapshot is null)
        {
            return new WaveLinkReconcileResult(0, 0, 0, 0, "Wave Link is not connected");
        }

        // Wave Link's outputDevices list can hold one entry per output bus on a single device.
        // Reconcile per unique device id so we don't repeat work for the same physical endpoint.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renderEndpoints = _endpoints.GetEndpoints(EndpointFlow.Render);

        var inspected = 0;
        var renamed = 0;
        var skipped = 0;
        var unmatched = 0;

        foreach (var output in snapshot.OutputDevices)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrEmpty(output.DeviceId)) continue;
            if (!seen.Add(output.DeviceId)) continue;

            inspected++;
            var endpoint = FindEndpoint(renderEndpoints, output.DeviceId, output.DeviceName);
            if (endpoint is null)
            {
                unmatched++;
                _logger.LogInformation("Reconcile: no Windows endpoint matches Wave Link device '{Name}' (id {Id})",
                    output.DeviceName, output.DeviceId);
                continue;
            }

            if (string.Equals(endpoint.FriendlyName, output.DeviceName, StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            if (_endpoints.SetFriendlyName(endpoint.Id, output.DeviceName))
            {
                renamed++;
            }
        }

        _logger.LogInformation("Reconcile: inspected={Inspected} renamed={Renamed} skipped={Skipped} unmatched={Unmatched}",
            inspected, renamed, skipped, unmatched);
        return new WaveLinkReconcileResult(inspected, renamed, skipped, unmatched, null);
    }

    private static AudioEndpoint? FindEndpoint(IReadOnlyList<AudioEndpoint> endpoints, string id, string name)
    {
        foreach (var e in endpoints)
        {
            if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return e;
            }
        }

        AudioEndpoint? nameMatch = null;
        foreach (var e in endpoints)
        {
            if (string.Equals(e.FriendlyName, name, StringComparison.Ordinal))
            {
                if (nameMatch is not null)
                {
                    // Ambiguous - two endpoints share the name. Skip rather than rename the wrong one.
                    return null;
                }
                nameMatch = e;
            }
        }
        return nameMatch;
    }
}
