using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Earmark.App.Services;

public interface INotificationService
{
    /// <summary>Registers the app with the Windows notification platform. Safe to call multiple
    /// times; subsequent calls are no-ops.</summary>
    void Register();

    /// <summary>Shows a simple two-line toast.</summary>
    void Show(string title, string body);
}

internal sealed class NotificationService : INotificationService, IDisposable
{
    private readonly ILogger<NotificationService> _logger;
    private bool _registered;

    public NotificationService(ILogger<NotificationService> logger)
    {
        _logger = logger;
    }

    public void Register()
    {
        if (_registered) return;

        // App notifications depend on the WindowsAppRuntime Singleton package, which a
        // self-contained deployment doesn't carry. The self-contained deployment guide says
        // to treat them as a light-up feature: probe IsSupported and skip quietly when the
        // platform isn't available rather than letting Register throw.
        var supported = AppNotificationManager.IsSupported();
        _logger.LogInformation("AppNotificationManager.IsSupported = {Supported}", supported);
        if (!supported)
        {
            _logger.LogInformation("App notifications unsupported on this deployment; skipping registration");
            return;
        }

        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
            _logger.LogInformation("AppNotificationManager registered");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AppNotificationManager.Register failed");
        }
    }

    public void Show(string title, string body)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentNullException.ThrowIfNull(body);
        if (!_registered)
        {
            Register();
            if (!_registered) return;
        }

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AppNotificationManager.Show failed");
        }
    }

    public void Dispose()
    {
        if (!_registered) return;
        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch
        {
            // Unregister can race with COM shutdown; swallow.
        }
    }
}
