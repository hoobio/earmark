using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.WaveLink;

namespace Earmark.Desktop.Tests;

internal static class SampleAudio
{
    public static readonly AudioEndpoint Headphones = new("{0.0.0.00000000}.{headphones}", "Headphones (Sample USB Audio)", "Sample USB Audio", EndpointFlow.Render, EndpointState.Active, true, false, "{c1}");
    public static readonly AudioEndpoint Speakers = new("{0.0.0.00000000}.{speakers}", "Speakers (Sample Realtek Audio)", "Sample Realtek Audio", EndpointFlow.Render, EndpointState.Active, false, true, "{c2}");
    public static readonly AudioEndpoint Monitor = new("{0.0.0.00000000}.{monitor}", "Monitor (Sample Display Audio)", "Sample Display Audio", EndpointFlow.Render, EndpointState.Active, false, false, "{c3}");
    public static readonly AudioEndpoint Microphone = new("{0.0.1.00000000}.{microphone}", "Microphone (Sample USB Audio)", "Sample USB Audio", EndpointFlow.Capture, EndpointState.Active, true, true, "{c1}");

    public static readonly AudioSession[] Sessions =
    [
        Session(990001, "Spotify", Headphones, SessionState.Active),
        Session(990002, "Discord", Headphones, SessionState.Active),
        Session(990003, "Firefox", Speakers, SessionState.Inactive),
    ];

    private static AudioSession Session(uint pid, string name, AudioEndpoint endpoint, SessionState state) =>
        new($"session:{pid}", $"identifier:{pid}", pid, name, $@"C:\Sample\{name}.exe", name, string.Empty, endpoint.Id, state, false);
}

internal sealed class SampleEndpoints : IAudioEndpointService
{
    private static readonly AudioEndpoint[] All = [SampleAudio.Headphones, SampleAudio.Speakers, SampleAudio.Monitor, SampleAudio.Microphone];

    public event EventHandler? EndpointsChanged { add { } remove { } }

    public event EventHandler? DefaultsChanged { add { } remove { } }

    public event EventHandler<EndpointMuteChangedEventArgs>? ExternalMuteChanged { add { } remove { } }

    public event EventHandler<EndpointVolumeChangedEventArgs>? ExternalVolumeChanged { add { } remove { } }

    public IReadOnlyList<AudioEndpoint> GetEndpoints(EndpointFlow flow = EndpointFlow.Render) => [.. All.Where(endpoint => endpoint.Flow == flow)];

    public AudioEndpoint? GetById(string id) => All.FirstOrDefault(endpoint => endpoint.Id == id);

    public float? GetVolume(string id) => 0.6f;

    public bool? GetMuted(string id) => id == SampleAudio.Monitor.Id;

    public bool SetVolume(string id, float level) => true;

    public bool SetMuted(string id, bool muted) => true;

    public void PlayTestPing(string id)
    {
    }

    public float? GetPeakLevel(string id) => 0.3f;

    public EndpointChannelPeaks? GetChannelPeaks(string id) => null;
}

internal sealed class SampleSessions : IAudioSessionService, IAudioSessionMeterService
{
    public event EventHandler? CaptureSessionsChanged { add { } remove { } }

    public event EventHandler<AudioSessionEvent>? SessionAdded { add { } remove { } }

    public event EventHandler<AudioSessionRemovedEvent>? SessionRemoved { add { } remove { } }

    public event EventHandler? SessionsChanged { add { } remove { } }

    public IReadOnlyList<AudioSession> GetSessions() => SampleAudio.Sessions;

    public IReadOnlyList<AudioSession> GetCaptureSessions() => [];

    public float? GetPeak(uint processId, string endpointId) => 0.2f;

    public void Refresh()
    {
    }
}

internal sealed class SampleProcesses : IRunningProcessProvider
{
    public event EventHandler<RunningProcessEvent>? ProcessStarted { add { } remove { } }

    public event EventHandler<RunningProcessEvent>? ProcessStopped { add { } remove { } }

    public IReadOnlyList<RunningProcess> GetRunningProcesses() => [.. SampleAudio.Sessions.Select(session => new RunningProcess(session.ProcessId, session.ProcessName, session.ExecutablePath))];
}

internal sealed class SampleNowPlaying : INowPlayingService
{
    private static readonly NowPlayingInfo Track = new("Spotify.exe", "spotify", "Sample Track", "Sample Artist", "Sample Album", NowPlayingStatus.Playing, true, true, true, true, 42, 0, 180, DateTime.UtcNow, null, string.Empty);

    public event EventHandler? Changed { add { } remove { } }

    public IReadOnlyList<NowPlayingInfo> GetSessions() => [Track];

    public NowPlayingInfo? GetPrimary() => Track;

    public Task TogglePlayPauseAsync(string sessionKey) => Task.CompletedTask;

    public Task NextAsync(string sessionKey) => Task.CompletedTask;

    public Task PreviousAsync(string sessionKey) => Task.CompletedTask;

    public Task SeekAsync(string sessionKey, double positionSeconds) => Task.CompletedTask;
}

internal sealed class NoWaveLink : IWaveLinkService
{
    public event EventHandler? StateChanged { add { } remove { } }

    public event EventHandler? SnapshotChanged { add { } remove { } }

    public bool IsEnabled { get; set; }

    public WaveLinkConnectionState State => WaveLinkConnectionState.Disabled;

    public bool IsAvailable => false;

    public WaveLinkSnapshot? LastSnapshot => null;

    public Task<WaveLinkSnapshot?> GetSnapshotAsync(CancellationToken ct = default) => Task.FromResult<WaveLinkSnapshot?>(null);

    public Task<bool> SetMixForOutputAsync(string deviceId, string outputId, string mixId, CancellationToken ct = default) => Task.FromResult(false);

    public Task<bool> SetMixMutedAsync(string mixId, bool muted, CancellationToken ct = default) => Task.FromResult(false);

    public Task<bool> SetMixLevelAsync(string mixId, float level, CancellationToken ct = default) => Task.FromResult(false);

    public Task<bool> SetInputDeviceMutedAsync(string deviceId, string inputId, bool muted, CancellationToken ct = default) => Task.FromResult(false);
}

internal sealed class NoBluetooth : IBluetoothAudioControl
{
    public bool IsBluetooth(string endpointId) => endpointId == SampleAudio.Headphones.Id;

    public void RequestConnect(string endpointId)
    {
    }

    public void RequestDisconnect(string endpointId)
    {
    }
}

internal sealed class NoPolicy : IAudioPolicyService
{
    public void SetDefaultEndpointForApp(string sessionIdentifier, string endpointId, RoleScope role, EndpointFlow flow)
    {
    }

    public bool SetSystemDefaultEndpoint(string endpointId, EndpointFlow flow, RoleScope role) => true;
}
