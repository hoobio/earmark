namespace Earmark.App.Services;

/// <summary>Shows a brief, bottom-centred, auto-dismissing toast inside the Earmark window for feedback
/// tied to an interactive action the user just took there (e.g. "couldn't close that app"). Kept
/// distinct from <see cref="INotificationService"/> (Windows toasts), which is reserved for background
/// events the user may not be looking at - like a rule correcting a device's state.</summary>
public interface IInAppNotificationService
{
    /// <summary>Requests a toast. The hosting window (a singleton) renders it on the UI thread.</summary>
    void Show(string message);

    /// <summary>Raised when <see cref="Show"/> is called. The window subscribes once and presents the
    /// toast; nothing happens if the window isn't up yet.</summary>
    event EventHandler<string>? ToastRequested;
}

internal sealed class InAppNotificationService : IInAppNotificationService
{
    public event EventHandler<string>? ToastRequested;

    public void Show(string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        ToastRequested?.Invoke(this, message);
    }
}
