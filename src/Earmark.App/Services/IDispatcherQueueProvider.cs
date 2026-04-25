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
            action();
            return;
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
