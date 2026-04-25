using Earmark.App.Hosting;
using Earmark.App.Services;
using Earmark.App.ViewModels;
using Earmark.App.Views;
using Earmark.Audio;
using Earmark.Core;
using Earmark.Core.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace Earmark.App;

public partial class App : Application
{
    private IHost? _host;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    public static new App Current => (App)Application.Current;

    public IServiceProvider Services => _host?.Services
        ?? throw new InvalidOperationException("Host is not yet initialized.");

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _host = Host.CreateApplicationBuilder()
            .ConfigureEarmark()
            .Build();

        await _host.Services.GetRequiredService<IRulesService>().LoadAsync();
        _host.Services.GetRequiredService<IRoutingApplier>().Start();

        _window = _host.Services.GetRequiredService<MainWindow>();
        _window.Closed += async (_, _) => await DisposeHostAsync();
        _window.Activate();
    }

    private async Task DisposeHostAsync()
    {
        if (_host is null)
        {
            return;
        }

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
        var logger = _host?.Services.GetService<ILogger<App>>();
        logger?.LogCritical(e.Exception, "Unhandled exception");
        e.Handled = true;
    }
}
