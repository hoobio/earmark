using Earmark.Core.Models;

namespace Earmark.Core.Audio;

public sealed record AudioSessionEvent(AudioSession Session);

public sealed record AudioSessionRemovedEvent(uint ProcessId);

public interface IAudioSessionService
{
    IReadOnlyList<AudioSession> GetSessions();
    event EventHandler<AudioSessionEvent>? SessionAdded;
    event EventHandler<AudioSessionRemovedEvent>? SessionRemoved;
    event EventHandler? SessionsChanged;
}
