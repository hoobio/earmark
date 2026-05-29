using System.Globalization;

namespace Earmark.Core.Models;

/// <summary>
/// A running process Earmark could resolve identity for, independent of whether it currently
/// holds an audio session. Lets app rules pin (and the Home page show) an app that's running but
/// silent - the per-app endpoint preference is keyed by process identity, not by a live session.
/// </summary>
public sealed record RunningProcess(uint ProcessId, string ProcessName, string ExecutablePath)
{
    /// <summary>Identity shared with <see cref="AudioSession.IdentityKey"/> so a synthetic entry
    /// and the app's real session collapse to one chip / match.</summary>
    public string IdentityKey =>
        !string.IsNullOrEmpty(ExecutablePath) ? ExecutablePath.ToLowerInvariant()
        : !string.IsNullOrEmpty(ProcessName) ? ProcessName.ToLowerInvariant()
        : ProcessId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds a placeholder <see cref="AudioSession"/> for a process with no live audio session, so
    /// the rule matcher and chip pipeline (both keyed on <see cref="AudioSession"/>) treat a
    /// silent-but-running app like an audible one. State is <see cref="SessionState.Inactive"/> and
    /// there is no endpoint or meter - it carries only the fields matching needs (pid, name, path).
    /// </summary>
    public AudioSession ToSyntheticSession() => new(
        SessionInstanceId: $"running:{ProcessId.ToString(CultureInfo.InvariantCulture)}",
        SessionIdentifier: ProcessId.ToString(CultureInfo.InvariantCulture),
        ProcessId: ProcessId,
        ProcessName: ProcessName,
        ExecutablePath: ExecutablePath,
        DisplayName: string.IsNullOrEmpty(ProcessName) ? ExecutablePath : ProcessName,
        IconPath: string.Empty,
        CurrentEndpointId: string.Empty,
        State: SessionState.Inactive,
        IsSystemSounds: false);
}
