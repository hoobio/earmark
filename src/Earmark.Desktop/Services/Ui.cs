using Avalonia.Threading;

namespace Earmark.Desktop.Services;

public static class Ui
{
    public static void Post(Action action) => Dispatcher.UIThread.Post(action);
}
