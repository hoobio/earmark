using System.ComponentModel;
using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

using Microsoft.Extensions.Logging;

namespace Earmark.Desktop.Services;

public sealed class UiWatchdog(Window window, ILogger logger)
{
    public const string RestartAfterArgument = "--restart-after=";
    private const int MissedBeatsBeforeRestart = 6;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RelaunchGuard = TimeSpan.FromMinutes(5);

    private volatile int _stage;
    private volatile bool _visible = true;

    public static int? RestartedFrom(string[] args) =>
        args.FirstOrDefault(arg => arg.StartsWith(RestartAfterArgument, StringComparison.Ordinal)) is { } arg
        && int.TryParse(arg[RestartAfterArgument.Length..], out var pid) ? pid : null;

    public static void WaitForPreviousInstance(string[] args)
    {
        if (RestartedFrom(args) is not { } pid)
        {
            return;
        }

        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(TimeSpan.FromSeconds(15));
        }
        catch (ArgumentException)
        {
        }
    }

    public void Start()
    {
        if (Debugger.IsAttached)
        {
            logger.LogInformation("UI watchdog skipped: debugger attached");
            return;
        }

        new Thread(Run) { IsBackground = true, Name = "UI watchdog" }.Start();
        logger.LogInformation("UI watchdog started ({Interval}s beats, restart after {Missed} misses)", Interval.TotalSeconds, MissedBeatsBeforeRestart);
    }

    private void Run()
    {
        var missed = 0;
        while (true)
        {
            _stage = 0;
            Dispatcher.UIThread.Post(Beat);
            Thread.Sleep(Interval);
            while (_stage < 2)
            {
                if (++missed >= MissedBeatsBeforeRestart && !Debugger.IsAttached)
                {
                    Restart(_stage == 0 ? "UI thread" : "render thread");
                    return;
                }

                Thread.Sleep(Interval);
            }

            missed = 0;
        }
    }

    private void Beat()
    {
        _stage = 1;
        _visible = window.IsVisible;
        if (ElementComposition.GetElementVisual(window)?.Compositor is { } compositor)
        {
            _ = ProbeRenderThreadAsync(compositor);
        }
        else
        {
            _stage = 2;
        }
    }

    // The GPU interop job runs on the render thread only once its graphics context is valid, so its completion shows both threads are alive.
    private async Task ProbeRenderThreadAsync(Compositor compositor)
    {
        await compositor.TryGetCompositionGpuInterop();
        _stage = 2;
    }

    private void Restart(string stalled)
    {
        var restarted = RestartedFrom(Environment.GetCommandLineArgs()) is not null
            && DateTime.Now - Process.GetCurrentProcess().StartTime < RelaunchGuard;
        if (restarted)
        {
            logger.LogWarning("The {Stalled} stopped responding again within {Minutes} minutes of a restart; not restarting again", stalled, RelaunchGuard.TotalMinutes);
            return;
        }

        var mode = _visible ? StartupRegistration.NoActivateArgument : StartupRegistration.TrayArgument;
        logger.LogWarning("The {Stalled} stopped responding for {Seconds}s, most likely after a GPU driver reset; restarting with {Mode}", stalled, (Interval * MissedBeatsBeforeRestart).TotalSeconds, mode);
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { mode, $"{RestartAfterArgument}{Environment.ProcessId}" } });
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(e, "Could not start a new Earmark process; not restarting");
            return;
        }

        Process.GetCurrentProcess().Kill();
    }
}
