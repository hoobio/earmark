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
    bool IsSystemSounds);
