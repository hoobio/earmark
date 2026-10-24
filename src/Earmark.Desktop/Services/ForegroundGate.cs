namespace Earmark.Desktop.Services;

public sealed class ForegroundGate
{
    private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ForegroundGate() => _released.SetResult();

    public bool IsHeld => !_released.Task.IsCompleted;

    public Task WhenReleased => _released.Task;

    public void Hold()
    {
        if (!IsHeld)
        {
            _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public bool Release() => _released.TrySetResult();
}
