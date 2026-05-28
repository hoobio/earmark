namespace Earmark.Core.Audio;

/// <summary>
/// Per-session peak metering. Sessions are identified by owning PID because that's the
/// stable handle the UI knows (rules match on it, drag/drop carries it). Returns null
/// when no session for the PID is currently registered on any render endpoint.
/// </summary>
public interface IAudioSessionMeterService
{
    float? GetPeak(uint processId);
}
