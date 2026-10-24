using System.Diagnostics.CodeAnalysis;

using Avalonia.Threading;

namespace Earmark.Desktop.Services;

public sealed class SingleInstance : IDisposable
{
    private const string BaseName = "Local\\Earmark";
    private const string ShowEventName = $"{BaseName}.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private RegisteredWaitHandle? _showWait;

    private SingleInstance(Mutex mutex, EventWaitHandle show)
    {
        _mutex = mutex;
        _show = show;
    }

    public static bool TryAcquire(bool showRunning, [NotNullWhen(true)] out SingleInstance? instance)
    {
        var mutex = new Mutex(initiallyOwned: false, BaseName);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero);
        }
        // A killed process (watchdog restart, installer, Task Manager) leaves the mutex abandoned; the next launch still owns it.
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            if (showRunning && EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
            {
                using (show)
                {
                    show.Set();
                }
            }

            instance = null;
            return false;
        }

        instance = new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName));
        return true;
    }

    public void ListenForShow(Action show) =>
        _showWait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => Dispatcher.UIThread.Post(show), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _showWait?.Unregister(null);
        _mutex.ReleaseMutex();
        _mutex.Dispose();
        _show.Dispose();
    }
}
