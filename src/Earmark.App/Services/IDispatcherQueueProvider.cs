using Microsoft.UI.Dispatching;

namespace Earmark.App.Services;

public interface IDispatcherQueueProvider
{
    DispatcherQueue Queue { get; }
    void Register(DispatcherQueue queue);
    void Enqueue(Action action);
}

internal sealed class DispatcherQueueProvider : IDispatcherQueueProvider
{
    private DispatcherQueue? _queue;

    public DispatcherQueue Queue => _queue
        ?? throw new InvalidOperationException("Dispatcher has not been registered yet.");

    public void Register(DispatcherQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
    }

    public void Enqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_queue is null)
        {
            // Fail fast instead of running inline on the caller's thread. Callers (e.g. the
            // icon loader on a thread-pool thread) rely on this to marshal to the UI thread;
            // running off-thread would construct XAML objects on the wrong apartment. Register
            // always runs at window init before any Enqueue today, so this never fires.
            throw new InvalidOperationException("Dispatcher has not been registered yet.");
        }

        if (_queue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _queue.TryEnqueue(() => action());
        }
    }
}
