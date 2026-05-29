namespace Earmark.Core.Models;

public enum SessionState
{
    Inactive,
    Active,
    Expired,
}

public sealed record AudioSession(
    string SessionInstanceId,
    string SessionIdentifier,
    uint ProcessId,
    string ProcessName,
    string ExecutablePath,
    string DisplayName,
    string IconPath,
    string CurrentEndpointId,
    SessionState State,
    bool IsSystemSounds)
{
    /// <summary>Stable per-application key that collapses the several processes one app spawns
    /// (Discord, Chromium browsers) into a single chip / match count. The executable path is the
    /// app's true identity; the process name is the fallback when the path is unavailable, and
    /// the pid is the last resort so two unrelated path-less sessions never merge. Two same-named
    /// apps launched from different locations stay distinct because their paths differ.</summary>
    public string IdentityKey =>
        !string.IsNullOrEmpty(ExecutablePath) ? ExecutablePath.ToLowerInvariant()
        : !string.IsNullOrEmpty(ProcessName) ? ProcessName.ToLowerInvariant()
        : ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
