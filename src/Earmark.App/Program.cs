using System.Runtime.InteropServices;

using Earmark.App.Settings;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Earmark.App;

public static class Program
{
    private const string SingleInstanceKey = "Earmark.SingleInstance";

    [STAThread]
    public static int Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();

        if (!DecideRedirection())
        {
            Application.Start(_ =>
            {
                var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
                var ctx = new DispatcherQueueSynchronizationContext(dispatcherQueue);
                SynchronizationContext.SetSynchronizationContext(ctx);

                var app = new App
                {
                    LaunchToTrayRequested = StartupRegistration.LaunchedWithTrayFlag(args),
                };
            });
        }

        return 0;
    }

    private static bool DecideRedirection()
    {
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        var keyInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);

        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += OnActivatedFromOtherInstance;
            return false;
        }

        keyInstance.RedirectActivationToAsync(args).AsTask().GetAwaiter().GetResult();
        return true;
    }

    private static void OnActivatedFromOtherInstance(object? sender, AppActivationArguments e)
    {
        if (Application.Current is not App app)
        {
            return;
        }

        var dispatcher = app.MainDispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.TryEnqueue(() => app.RestoreFromBackground());
    }
}

internal static class ComWrappersSupport
{
    [DllImport("Microsoft.UI.Xaml.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void XamlCheckProcessRequirements();

    public static void InitializeComWrappers()
    {
        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
    }
}
