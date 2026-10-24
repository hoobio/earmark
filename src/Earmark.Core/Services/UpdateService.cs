using System.Text.Json;
using System.Text.Json.Serialization;

using Earmark.Core.Settings;

using Microsoft.Extensions.Logging;

namespace Earmark.Core.Services;

public enum UpdateStatus
{
    Unknown,
    Checking,
    UpToDate,
    UpdateAvailable,
    Failed,
    NotSupported,
}

public sealed class UpdateService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly ILogger<UpdateService> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateService(SettingsService settings, bool isPackaged, ILogger<UpdateService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        IsSupported = !isPackaged;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub's API rejects requests without a User-Agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Earmark/{AppInfo.Version}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        Status = isPackaged ? UpdateStatus.NotSupported : UpdateStatus.Unknown;
    }

    public UpdateStatus Status { get; private set; }

    public string? LatestVersion { get; private set; }

    public string? LatestReleaseUrl { get; private set; }

    public bool IsSupported { get; }

    // Raised on a thread-pool thread; UI subscribers must marshal to their own thread.
    public event EventHandler? StatusChanged;

    public async Task CheckForUpdatesAsync(bool manual, CancellationToken ct = default)
    {
        if (!IsSupported)
        {
            return;
        }

        // Dev builds share settings.json with a side-by-side live install, so they never touch the auto-check toggle.
        if (!manual && (AppInfo.IsDevBuild || !_settings.Current.CheckForUpdates))
        {
            return;
        }

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

            // Only a pre-release build considers pre-releases; every other channel sees stable releases only.
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
        StatusChanged?.Invoke(this, EventArgs.Empty);
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

internal readonly record struct SemVer(int Major, int Minor, int Patch, int Pre)
{
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
