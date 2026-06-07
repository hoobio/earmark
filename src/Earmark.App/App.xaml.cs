using Earmark.App.Hosting;
using Earmark.App.Services;
using Earmark.App.Settings;
using Earmark.App.ViewModels;
using Earmark.App.Views;
using Earmark.Audio;
using Earmark.Core;
using Earmark.Core.Audio;
using Earmark.Core.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Earmark.App;

public partial class App : Application
{
    private IHost? _host;
    private MainWindow? _window;
    private ILogger<App>? _logger;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    public static new App Current => (App)Application.Current;

    public IServiceProvider Services => _host?.Services
        ?? throw new InvalidOperationException("Host is not yet initialized.");

    public bool LaunchToTrayRequested { get; set; }

    public DispatcherQueue? MainDispatcher { get; private set; }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainDispatcher = DispatcherQueue.GetForCurrentThread();

        _host = Host.CreateApplicationBuilder()
            .ConfigureEarmark()
            .Build();

        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("Earmark starting; log file: {LogPath}", HostBuilderExtensions.CurrentLogPath);

        try
        {
            await _host.Services.GetRequiredService<ISettingsService>().LoadAsync();
            _logger.LogInformation("Settings loaded");

            // Heavy init runs on the thread pool so the UI thread can paint immediately. The first
            // navigation is gated only on what the pages need to bind: the audio singletons (whose
            // COM-heavy construction must not run during page nav on the UI thread) and the loaded
            // rules. Seeding and the routing applier's first apply are NOT on the gate - they're
            // COM-heavy too but the Devices grid can paint without them, so they run ungated after,
            // shrinking the blank-window window. navGate is completed the moment those bindings are
            // ready, even if a step faults (we navigate to a degraded UI rather than hang).
            var navGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var initTask = Task.Run(async () =>
            {
                try
                {
                    // Apply persisted startup settings (file log level, WaveLink enable, run-at-login)
                    // off the UI thread: the WaveLink enable triggers a TCP connect we don't want on
                    // the first-paint path, and it must run before the routing applier's first apply.
                    _host.Services.GetRequiredService<StartupSettingsApplier>().Start();

                    // Construct the audio singletons here (off the UI thread). Time each so a slow
                    // machine shows where startup goes. The meter service is pre-built too:
                    // HomeViewModel needs it, and otherwise its Rebuild() would run on the UI thread.
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    _ = _host.Services.GetRequiredService<IAudioEndpointService>();
                    var epMs = sw.ElapsedMilliseconds;
                    _ = _host.Services.GetRequiredService<IAudioSessionService>();
                    var sessMs = sw.ElapsedMilliseconds - epMs;
                    _ = _host.Services.GetRequiredService<IAudioSessionMeterService>();
                    var meterMs = sw.ElapsedMilliseconds - epMs - sessMs;
                    _logger.LogInformation(
                        "Audio services initialized in {TotalMs} ms (endpoints {EpMs}, sessions {SessMs}, meters {MeterMs})",
                        sw.ElapsedMilliseconds, epMs, sessMs, meterMs);

                    await _host.Services.GetRequiredService<IRulesService>().LoadAsync();
                    _logger.LogInformation("Rules loaded");
                }
                finally
                {
                    // Unblock the first navigation as soon as the pages can bind, even on fault.
                    navGate.TrySetResult();
                }

                // Blank-slate installs only: seed the starter device groups + two disabled example
                // rules. A no-op when any rule or Devices-page config already exists, so it never
                // wipes an existing layout. Wave Link groups seed only if Wave Link is already
                // connected (off by default on a fresh install).
                await _host.Services.GetRequiredService<IDeviceDefaultsService>().SeedDefaultsIfEmptyAsync();

                _host.Services.GetRequiredService<IRoutingApplier>().Start();
                _logger.LogInformation("Routing applier started");
            });

            _window = _host.Services.GetRequiredService<MainWindow>();
            _window.InitializationTask = navGate.Task;
            // Sync handler on purpose: WinUI stops pumping the dispatcher once the last
            // window closes, so an `async void` continuation containing _host.Dispose()
            // can be dropped on the floor and leak COM resources / the IMMNotificationClient
            // registration, which manifests as the process hanging after close.
            _window.Closed += (_, _) => DisposeHost();

            var chrome = _host.Services.GetRequiredService<IWindowChromeManager>();
            chrome.Attach(_window);

            // Wire the taskbar thumbnail toolbar (prev / play-pause / next) before Activate so the
            // subclass catches the first TaskbarButtonCreated message.
            _host.Services.GetRequiredService<ITaskbarMediaControls>().Attach(_window);

            // Show the window NOW with its loading skeleton. Everything not needed to paint the
            // shell (notification registration, the Quick Controls hotkey window, the update check)
            // is deferred to a low-priority dispatch that runs after the first frame, so nothing
            // here blocks the window appearing. The content area shows MainWindow's skeleton until
            // background init completes and the first page navigates.
            var settings = _host.Services.GetRequiredService<ISettingsService>().Current;
            var startHidden = LaunchToTrayRequested || settings.LaunchToTray;
            _window.Activate();
            if (startHidden && settings.ShowTrayIcon)
            {
                chrome.HideToTray();
                _logger.LogInformation("Main window started hidden in tray");
            }
            else
            {
                _logger.LogInformation("Main window activated");
            }

            _ = initTask.ContinueWith(
                t => _logger.LogError(t.Exception, "Background startup failed"),
                TaskContinuationOptions.OnlyOnFaulted);

            // Deferred, post-first-paint. The update check self-gates on packaged identity + the
            // CheckForUpdates setting.
            MainDispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                _host.Services.GetRequiredService<INotificationService>().Register();
                _host.Services.GetRequiredService<IQuickControlsService>().Start();
                _ = _host.Services.GetRequiredService<IUpdateService>().CheckForUpdatesAsync(manual: false);
            });
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Startup failed");
            throw;
        }
    }

    public void RestoreFromBackground()
    {
        if (_host is null)
        {
            return;
        }

        var chrome = _host.Services.GetService<IWindowChromeManager>();
        chrome?.RestoreWindow();
    }

    /// <summary>Restores the main window, navigates to Settings, and reveals the Quick Controls section.
    /// Invoked from the Quick Controls overlay's "Settings" context-menu item (cards and group titles).</summary>
    public void OpenQuickControlsSettings()
    {
        if (_host is null)
        {
            return;
        }

        RestoreFromBackground();
        _host.Services.GetRequiredService<MainWindow>().NavigateByTag("Settings");
        _host.Services.GetRequiredService<SettingsPage>().RevealQuickControls();
    }

    public void DisposeHost()
    {
        if (_host is null)
        {
            return;
        }

        var host = _host;
        _host = null;

        _logger?.LogInformation("Shutting down host");

        // No IHostedService / lifetime subscribers are registered, so StopAsync would just be a
        // no-op that blocks the UI thread up to its timeout on close. host.Dispose() alone
        // disposes the singletons (which release their COM). Re-add StopAsync if a hosted
        // service is ever registered.
        try
        {
            host.Dispose();
        }
        catch
        {
            // Ignore disposal failures.
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unhandled exception");
        System.Diagnostics.Debug.WriteLine($"[Earmark] Unhandled: {e.Exception}");

        if (_window?.Content?.XamlRoot is { } root)
        {
            _ = ShowErrorAsync(root, e.Exception);
        }

        e.Handled = true;
    }

    private static async Task ShowErrorAsync(Microsoft.UI.Xaml.XamlRoot root, Exception ex)
    {
        try
        {
            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                XamlRoot = root,
                Title = "Earmark hit an error",
                Content = ex.ToString(),
                CloseButtonText = "Dismiss",
            };

            await dialog.ShowAsync();
        }
        catch
        {
            // Last-ditch silent swallow.
        }
    }
}
