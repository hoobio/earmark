using Earmark.Core;
using Earmark.Core.Logging;
using Earmark.Core.Settings;
using Earmark.Core.WaveLink;

using Microsoft.Extensions.Logging;

namespace Earmark.Audio.Services;

public sealed class StartupSettingsApplier : IDisposable
{
    private readonly SettingsService _settings;
    private readonly FileLoggerProvider _fileLogger;
    private readonly IWaveLinkService _waveLink;
    private readonly ILogger<StartupSettingsApplier> _logger;
    private bool _started;

    public StartupSettingsApplier(
        SettingsService settings,
        FileLoggerProvider fileLogger,
        IWaveLinkService waveLink,
        ILogger<StartupSettingsApplier> logger)
    {
        _settings = settings;
        _fileLogger = fileLogger;
        _waveLink = waveLink;
        _logger = logger;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        if (!AppInfo.IsRelease)
        {
            _logger.LogInformation("Launch on startup is not written for the {Channel} channel; only Release builds own the Run value", AppInfo.BuildChannel);
        }

        _settings.SettingsChanged += OnChanged;
        Apply();
    }

    private void OnChanged(object? sender, EventArgs e) => Apply();

    private void Apply()
    {
        try
        {
            var s = _settings.Current;
            // The Run value is shared by every channel, and blocked settings are defaults, so neither may rewrite or delete the installed Release entry.
            if (AppInfo.IsRelease && !_settings.IsSaveBlocked)
            {
                if (s.LaunchOnStartup)
                {
                    StartupRegistration.Register(s.LaunchToTray);
                }
                else
                {
                    StartupRegistration.Unregister();
                }
            }

            var desired = s.VerboseLogging ? LogLevel.Debug : LogLevel.Information;
            if (_fileLogger.MinimumLevel != desired)
            {
                _fileLogger.SetMinimumLevel(desired);
                _logger.LogInformation("File log level set to {Level}", desired);
            }

            if (_waveLink.IsEnabled != s.EnableWaveLink)
            {
                _waveLink.IsEnabled = s.EnableWaveLink;
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
