using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

using Earmark.App.Settings;

using Microsoft.Extensions.Logging;

namespace Earmark.App.Services;

public enum UpdateStatus
{
    /// <summary>No check has run yet.</summary>
    Unknown,
    Checking,
    UpToDate,
    UpdateAvailable,
    /// <summary>The last check errored (offline, rate-limited, parse failure).</summary>
    Failed,
    /// <summary>Packaged (MSIX/Store) build: updates are managed by the Store, so we never check.</summary>
    NotSupported,
}

public interface IUpdateService
{
    UpdateStatus Status { get; }

    /// <summary>Display name of the latest release once a check has found one (e.g. "v0.1.9").</summary>
    string? LatestVersion { get; }

    /// <summary>Browser URL of the latest release, set once a check completes.</summary>
    string? LatestReleaseUrl { get; }

    /// <summary>False for packaged builds, which update through the Microsoft Store.</summary>
    bool IsSupported { get; }

    event EventHandler? StatusChanged;

    /// <summary>
    /// Checks the latest stable GitHub release. <paramref name="manual"/> checks run even when the
    /// auto-check setting is off (the user asked); auto checks respect it. No-op on packaged builds.
    /// Never throws - failures land as <see cref="UpdateStatus.Failed"/>.
    /// </summary>
    Task CheckForUpdatesAsync(bool manual, CancellationToken ct = default);
}

internal sealed class UpdateService : IUpdateService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IDispatcherQueueProvider _dispatcher;
    private readonly ILogger<UpdateService> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateService(
        ISettingsService settings,
        IDispatcherQueueProvider dispatcher,
        ILogger<UpdateService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub's API rejects requests without a User-Agent; the Accept header opts into the
        // stable v3 media type.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Earmark/{AppInfo.Version}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        Status = AppInfo.IsPackaged ? UpdateStatus.NotSupported : UpdateStatus.Unknown;
    }

    public UpdateStatus Status { get; private set; }

    public string? LatestVersion { get; private set; }

    public string? LatestReleaseUrl { get; private set; }

    public bool IsSupported => !AppInfo.IsPackaged;

    public event EventHandler? StatusChanged;

    public async Task CheckForUpdatesAsync(bool manual, CancellationToken ct = default)
    {
        if (!IsSupported)
        {
            return;
        }

        // Auto-checks are skipped for local (Dev) builds and when the setting is off. We never read
        // or write the persisted setting for a Dev build: it shares settings.json with any
        // side-by-side live install, so it must not influence that toggle. Manual checks always run.
        if (!manual && (AppInfo.IsDevBuild || !_settings.Current.CheckForUpdates))
        {
            return;
        }

        // Drop overlapping checks rather than queueing them.
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            SetStatus(UpdateStatus.Checking);

            using var response = await _http.GetAsync(AppInfo.ReleasesApiUrl, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var releases = await JsonSerializer
                .DeserializeAsync(stream, GitHubJsonContext.Default.GitHubReleaseArray, ct)
                .ConfigureAwait(false);

            if (!SemVer.TryParse(AppInfo.Version, out var current))
            {
                _logger.LogWarning("Update check: could not parse current version '{Version}'", AppInfo.Version);
                SetStatus(UpdateStatus.Failed);
                return;
            }

            // A pre-release build tracks the newest release of any kind (it sees the next -pre.N,
            // but a newer stable still wins by semver precedence). Every other channel only
            // considers stable releases, so a pre-release never surfaces as an update there.
            GitHubRelease? best = null;
            var bestSem = default(SemVer);
            foreach (var release in releases ?? [])
            {
                if (release.Draft)
                {
                    continue;
                }

                if (!AppInfo.IsPrerelease && release.Prerelease)
                {
                    continue;
                }

                if (SemVer.TryParse(release.TagName, out var sem) && (best is null || sem.CompareTo(bestSem) > 0))
                {
                    best = release;
                    bestSem = sem;
                }
            }

            if (best is null)
            {
                _logger.LogWarning("Update check: no comparable releases found");
                SetStatus(UpdateStatus.Failed);
                return;
            }

            LatestVersion = best.Name is { Length: > 0 } name ? name : best.TagName;
            LatestReleaseUrl = string.IsNullOrEmpty(best.HtmlUrl) ? AppInfo.ReleasesPageUrl : best.HtmlUrl;

            var newer = bestSem.CompareTo(current) > 0;
            _logger.LogInformation(
                "Update check ({Channel}): current {Current}, latest {Latest} -> {Result}",
                AppInfo.BuildChannel, AppInfo.Version, best.TagName, newer ? "update available" : "up to date");
            SetStatus(newer ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate);
        }
        catch (OperationCanceledException)
        {
            // Shutdown or timeout - leave the prior status untouched.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            SetStatus(UpdateStatus.Failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetStatus(UpdateStatus status)
    {
        Status = status;
        _dispatcher.Enqueue(() => StatusChanged?.Invoke(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}

[JsonSerializable(typeof(GitHubRelease[]))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }
}

/// <summary>
/// Minimal semver for the tags this repo produces: "vX.Y.Z" and "vX.Y.Z-pre.N". A stable release
/// sorts above any pre-release of the same X.Y.Z; pre-releases order by their trailing number.
/// </summary>
internal readonly record struct SemVer(int Major, int Minor, int Patch, int Pre)
{
    // Stable releases have no pre-release component; sort them above every pre-release.
    private const int StablePre = int.MaxValue;

    public static bool TryParse(string? tag, out SemVer value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var s = tag.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
        {
            s = s[1..];
        }

        var dash = s.IndexOf('-');
        var core = dash >= 0 ? s[..dash] : s;
        var pre = dash >= 0 ? s[(dash + 1)..] : null;

        var parts = core.Split('.');
        if (parts.Length < 3
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || !int.TryParse(parts[2], out var patch))
        {
            return false;
        }

        var preNumber = StablePre;
        if (pre is not null)
        {
            // "pre.42" -> 42; anything non-numeric sorts as the lowest pre-release.
            var lastDot = pre.LastIndexOf('.');
            var trailing = lastDot >= 0 ? pre[(lastDot + 1)..] : pre;
            preNumber = int.TryParse(trailing, out var n) ? n : 0;
        }

        value = new SemVer(major, minor, patch, preNumber);
        return true;
    }

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }

        c = Minor.CompareTo(other.Minor);
        if (c != 0)
        {
            return c;
        }

        c = Patch.CompareTo(other.Patch);
        return c != 0 ? c : Pre.CompareTo(other.Pre);
    }
}
