using Earmark.Core.Models;

namespace Earmark.Core.Audio;

public sealed record AudioSessionEvent(AudioSession Session);

public interface IAudioSessionService
{
    IReadOnlyList<AudioSession> GetSessions();
    event EventHandler<AudioSessionEvent>? SessionAdded;
    event EventHandler? SessionsChanged;
}
