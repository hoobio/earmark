using Earmark.App.Hosting;
using Earmark.App.Services;
using Earmark.App.Settings;
using Earmark.App.ViewModels;
using Earmark.App.Views;
using Earmark.Audio;
using Earmark.Core;
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
            _host.Services.GetRequiredService<StartupSettingsApplier>().Start();
            _logger.LogInformation("Settings loaded");

            await _host.Services.GetRequiredService<IRulesService>().LoadAsync();
            _logger.LogInformation("Rules loaded");

            _host.Services.GetRequiredService<IRoutingApplier>().Start();
            _logger.LogInformation("Routing applier started");

            _window = _host.Services.GetRequiredService<MainWindow>();
            _window.Closed += async (_, _) => await DisposeHostAsync();

            var chrome = _host.Services.GetRequiredService<IWindowChromeManager>();
            chrome.Attach(_window);

            var settings = _host.Services.GetRequiredService<ISettingsService>().Current;
            var startHidden = LaunchToTrayRequested || settings.LaunchToTray;
            if (startHidden && settings.ShowTrayIcon)
            {
                _window.Activate();
                chrome.HideToTray();
                _logger.LogInformation("Main window started hidden in tray");
            }
            else
            {
                _window.Activate();
                _logger.LogInformation("Main window activated");
            }
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

    private async Task DisposeHostAsync()
    {
        if (_host is null)
        {
            return;
        }

        _logger?.LogInformation("Shutting down host");

        try
        {
            await _host.StopAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore shutdown failures.
        }

        _host.Dispose();
        _host = null;
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
