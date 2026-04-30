namespace Earmark.Core.WaveLink;

public enum WaveLinkConnectionState
{
    /// <summary>Integration is turned off in settings.</summary>
    Disabled,

    /// <summary>Integration is on but Wave Link isn't reachable (not running, refused, etc.).</summary>
    Unavailable,

    /// <summary>Integration is on and the WS is open.</summary>
    Connected,
}

public interface IWaveLinkService
{
    bool IsEnabled { get; set; }

    WaveLinkConnectionState State { get; }

    bool IsAvailable { get; }

    /// <summary>Most recent snapshot pulled by GetSnapshotAsync. Null if integration is disabled or never succeeded.</summary>
    WaveLinkSnapshot? LastSnapshot { get; }

    event EventHandler? StateChanged;

    event EventHandler? SnapshotChanged;

    Task<WaveLinkSnapshot?> GetSnapshotAsync(CancellationToken ct = default);

    Task<bool> SetMixForOutputAsync(string deviceId, string outputId, string mixId, CancellationToken ct = default);
}

public sealed record WaveLinkSnapshot(
    IReadOnlyList<WaveLinkMixInfo> Mixes,
    IReadOnlyList<WaveLinkOutputInfo> OutputDevices);

public sealed record WaveLinkMixInfo(string Id, string Name);

public sealed record WaveLinkOutputInfo(
    string DeviceId,
    string OutputId,
    string DeviceName,
    string CurrentMixId);
