using Microsoft.Extensions.Logging;

namespace Earmark.Core.Persistence;

internal sealed class BlockedSaveLog(ILogger? logger)
{
    private long _next;

    public void Warn(string path)
    {
        var now = Environment.TickCount64;
        if (now < _next)
        {
            return;
        }

        _next = now + 60_000;
        logger?.LogWarning("Save skipped: {Path} failed to load, so saves stay blocked this session", path);
    }
}
