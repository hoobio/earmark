namespace Earmark.Core.Audio;

/// <summary>
/// Per-session peak metering. Keyed by (PID, endpoint) because a single process can
/// register sessions on multiple endpoints (apps that enumerate output devices in
/// shared mode do this) but only one of those sessions is actually producing audio.
/// Querying per-endpoint lets the UI place the chip on the right card. Returns null
/// when no session for that pid+endpoint pair is currently registered.
/// </summary>
public interface IAudioSessionMeterService
{
    float? GetPeak(uint processId, string endpointId);
}
