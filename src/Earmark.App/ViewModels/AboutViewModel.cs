using System.Diagnostics;

using CommunityToolkit.Mvvm.ComponentModel;

using Earmark.App.Hosting;
using Earmark.App.Services;
using Earmark.App.Settings;

using Microsoft.Extensions.Logging;

namespace Earmark.App.ViewModels;

/// <summary>
/// Backs the About + Help sections of the Settings page: version/build display, the GitHub help
/// links, the "open logs folder" button, and the update check (hidden entirely on packaged builds,
/// which update through the Store).
/// </summary>
public partial class AboutViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IUpdateService _update;
    private readonly ILogger<AboutViewModel> _logger;
    private bool _suppress;

    public AboutViewModel(ISettingsService settings, IUpdateService update, ILogger<AboutViewModel> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _update = update ?? throw new ArgumentNullException(nameof(update));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _suppress = true;
        AutoCheckForUpdates = _settings.Current.CheckForUpdates;
        _suppress = false;

        BuildInfo = ComposeBuildInfo();
        _update.StatusChanged += OnUpdateStatusChanged;
    }

    /// <summary>Two-line "About" detail: version (+ commit) on the first line, copyright on the second.</summary>
    public string BuildInfo { get; }

    private static string ComposeBuildInfo()
    {
        var version = $"Version {AppInfo.DisplayVersion}";
        if (AppInfo.ShortCommit is { } commit)
        {
            version += $" · {commit}";
        }

        return $"{version}\n{AppInfo.Copyright}";
    }

    /// <summary>False on packaged builds; the whole update UX is hidden when false.</summary>
    public bool IsUpdateCheckSupported => _update.IsSupported;

    public bool IsChecking => _update.Status == UpdateStatus.Checking;

    public bool UpdateAvailable => _update.Status == UpdateStatus.UpdateAvailable;

    public string UpdateStatusText => AppInfo.IsDevBuild
        ? "Update checks are off for local (Dev) builds."
        : _update.Status switch
        {
            UpdateStatus.Checking => "Checking for updates…",
            UpdateStatus.UpToDate => "You're on the latest version.",
            UpdateStatus.UpdateAvailable => $"Update available: {_update.LatestVersion}",
            UpdateStatus.Failed => "Couldn't check for updates. Try again later.",
            _ => string.Empty,
        };

    /// <summary>Whether the "Check now" button is enabled. Off mid-check and on Dev builds (which
    /// never reach the network).</summary>
    public bool CanCheck => !IsChecking && !AppInfo.IsDevBuild;

    /// <summary>Whether update checks run on this build at all. False on local (Dev) builds, where the
    /// whole update section (manual check and auto-check) is read-only. Settings cards bind IsEnabled to
    /// this so the section greys out as one, not just the buttons inside it.</summary>
    public bool IsUpdateCheckEnabled => IsUpdateCheckSupported && !AppInfo.IsDevBuild;

    /// <summary>
    /// Whether the auto-check toggle is interactive. Off for local (Dev) builds: they never
    /// auto-check, and a dev build shares settings.json with a side-by-side live install, so the
    /// toggle stays read-only to avoid flipping that shared setting.
    /// </summary>
    public bool IsAutoCheckEnabled => IsUpdateCheckEnabled;

    public string AutoCheckDescription => IsAutoCheckEnabled
        ? "Earmark checks GitHub for a newer release when it starts."
        : "Off for local (Dev) builds.";

    [ObservableProperty]
    public partial bool AutoCheckForUpdates { get; set; }

    partial void OnAutoCheckForUpdatesChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        _settings.Current.CheckForUpdates = value;
        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        try { await _settings.SaveAsync(); }
        catch { /* SettingsService logs/retries internally; never crash the UI thread. */ }
    }

    public Task CheckForUpdatesAsync() => _update.CheckForUpdatesAsync(manual: true);

    public void OpenLatestRelease() => OpenUrl(_update.LatestReleaseUrl ?? AppInfo.ReleasesPageUrl);

    public void OpenReleases() => OpenUrl(AppInfo.ReleasesPageUrl);

    public void ReportBug() => OpenUrl(AppInfo.NewBugUrl);

    public void RequestFeature() => OpenUrl(AppInfo.NewFeatureUrl);

    public void OpenGitHub() => OpenUrl(AppInfo.RepoUrl);

    public void Donate() => OpenUrl(AppInfo.DonateUrl);

    public void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(HostBuilderExtensions.LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{HostBuilderExtensions.LogDirectory}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open logs folder");
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open URL {Url}", url);
        }
    }

    private void OnUpdateStatusChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(UpdateAvailable));
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(CanCheck));
    }

    public void Dispose() => _update.StatusChanged -= OnUpdateStatusChanged;
}
