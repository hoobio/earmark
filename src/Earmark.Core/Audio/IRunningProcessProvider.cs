using Earmark.Core.Models;

namespace Earmark.Core.Audio;

public sealed record RunningProcessEvent(RunningProcess Process);

/// <summary>
/// Tracks the set of running processes so app rules can pin (and the Home page can show) an app
/// that's running but not currently playing audio. The audio-session layer only sees an app once
/// it opens a render/capture session; this fills the gap for the silent-but-running case.
/// </summary>
public interface IRunningProcessProvider
{
    /// <summary>Snapshot of currently running processes (one entry per pid). Served from the
    /// tracker's cached set, not a fresh scan, so it's cheap to call from an apply pass.</summary>
    IReadOnlyList<RunningProcess> GetRunningProcesses();

    /// <summary>Raised shortly after a new process appears.</summary>
    event EventHandler<RunningProcessEvent>? ProcessStarted;

    /// <summary>Raised shortly after a tracked process exits.</summary>
    event EventHandler<RunningProcessEvent>? ProcessStopped;
}
