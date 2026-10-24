using System.Diagnostics;

using Earmark.Core;
using Earmark.Core.Services;
using Earmark.Core.Settings;
using Earmark.Desktop.Hosting;
using Earmark.Desktop.Services;

using Microsoft.Extensions.Logging;

namespace Earmark.Desktop.ViewModels;

public sealed class AboutViewModel : NotifyBase
{
    private readonly SettingsService _settings;
    private readonly UpdateService _update;
    private readonly ILogger<AboutViewModel> _logger;

    public AboutViewModel(SettingsService settings, UpdateService update, ILogger<AboutViewModel> logger)
    {
        _settings = settings;
        _update = update;
        _logger = logger;
        update.StatusChanged += (_, _) => Ui.Post(() =>
        {
            Raise(nameof(UpdateStatusText));
            Raise(nameof(CanCheck));
            Raise(nameof(UpdateAvailable));
        });
        settings.SettingsChanged += (_, _) => Ui.Post(() => Raise(nameof(AutoCheck)));
    }

    public static string BuildInfo => $"Version {AppInfo.DisplayVersion}{(AppInfo.ShortCommit is { } commit ? $" - {commit}" : string.Empty)}\n{AppInfo.Copyright}";

    public bool IsUpdateSupported => _update.IsSupported;

    public bool UpdateAvailable => _update.Status == UpdateStatus.UpdateAvailable;

    public bool CanCheck => _update.Status != UpdateStatus.Checking && !AppInfo.IsDevBuild;

    public static bool CanAutoCheck => !AppInfo.IsDevBuild;

    public static string AutoCheckDescription => AppInfo.IsDevBuild ? "Off for local (Dev) builds." : "Earmark checks GitHub for a newer release when it starts.";

    public string UpdateStatusText => AppInfo.IsDevBuild
        ? "Update checks are off for local (Dev) builds."
        : _update.Status switch
        {
            UpdateStatus.Checking => "Checking for updates...",
            UpdateStatus.UpToDate => "You are on the latest version.",
            UpdateStatus.UpdateAvailable => $"Update available: {_update.LatestVersion}",
            UpdateStatus.Failed => "Could not check for updates. Try again later.",
            _ => string.Empty,
        };

    public bool AutoCheck
    {
        get => _settings.Current.CheckForUpdates;
        set
        {
            _settings.Current.CheckForUpdates = value;
            _ = _settings.SaveAsync();
        }
    }

    public Task CheckAsync() => _update.CheckForUpdatesAsync(manual: true);

    public void OpenLatestRelease() => Open(_update.LatestReleaseUrl ?? AppInfo.ReleasesPageUrl);

    public void OpenReleases() => Open(AppInfo.ReleasesPageUrl);

    public void ReportBug() => Open(AppInfo.NewBugUrl);

    public void RequestFeature() => Open(AppInfo.NewFeatureUrl);

    public void OpenGitHub() => Open(AppInfo.RepoUrl);

    public void Donate() => Open(AppInfo.DonateUrl);

    public void OpenLogs()
    {
        Directory.CreateDirectory(ServiceRegistration.LogDirectory);
        Open(ServiceRegistration.LogDirectory);
    }

    private void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open {Target}", target);
        }
    }
}
