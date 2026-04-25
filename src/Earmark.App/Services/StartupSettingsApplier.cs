using Earmark.App.Settings;

using Microsoft.Extensions.Logging;

namespace Earmark.App.Services;

internal sealed class StartupSettingsApplier : IDisposable
{
    private readonly ISettingsService _settings;
    private readonly ILogger<StartupSettingsApplier> _logger;
    private bool _started;

    public StartupSettingsApplier(ISettingsService settings, ILogger<StartupSettingsApplier> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _settings.SettingsChanged += OnChanged;
        Apply();
    }

    private void OnChanged(object? sender, EventArgs e) => Apply();

    private void Apply()
    {
        try
        {
            var s = _settings.Current;
            if (s.LaunchOnStartup)
            {
                StartupRegistration.Register(s.LaunchToTray);
            }
            else
            {
                StartupRegistration.Unregister();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying startup settings failed");
        }
    }

    public void Dispose()
    {
        if (_started)
        {
            _settings.SettingsChanged -= OnChanged;
            _started = false;
        }
    }
}
