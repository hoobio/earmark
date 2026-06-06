using System.Reflection;
using System.Runtime.InteropServices;

namespace Earmark.App.Services;

/// <summary>
/// Static facts about the running build: the release-please-managed version (baked into the
/// assembly via <c>version.txt</c> -&gt; <c>&lt;Version&gt;</c>), the build channel, the source
/// commit, and whether we're running packaged (MSIX). Drives the About section and gates the
/// update check (packaged builds update through the Store, so the check is hidden there).
/// </summary>
internal static class AppInfo
{
    private const string Owner = "hoobio";
    private const string Repo = "earmark";

    static AppInfo()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // InformationalVersion is "<semver>[+<commit-sha>]". Split off the commit, then strip any
        // semver pre-release suffix ("-pre") so the numeric part parses cleanly.
        var plusIndex = informational?.IndexOf('+') ?? -1;
        var versionPart = plusIndex >= 0 ? informational![..plusIndex] : informational;
        CommitHash = plusIndex >= 0 ? informational![(plusIndex + 1)..] : null;

        // Keeps any "-pre.N" suffix: a pre-release build's identity is its full semver, which the
        // update check needs to compare against newer pre-release tags.
        Version = string.IsNullOrWhiteSpace(versionPart)
            ? assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : versionPart;

        BuildChannel = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildChannel")?.Value ?? "Dev";

        Copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "Copyright (c) Hoobi";
    }

    /// <summary>The release-please version, e.g. "0.1.8". No commit suffix.</summary>
    public static string Version { get; }

    /// <summary>Short-ish source commit the build was produced from, or null when unavailable.</summary>
    public static string? CommitHash { get; }

    /// <summary>"Dev" (local), "Prerelease" (release-please PR build), or "Release" (CI).</summary>
    public static string BuildChannel { get; }

    /// <summary>Copyright string from the assembly (set via Directory.Build.props).</summary>
    public static string Copyright { get; }

    public static bool IsDevBuild => string.Equals(BuildChannel, "Dev", StringComparison.OrdinalIgnoreCase);

    public static bool IsPrerelease => string.Equals(BuildChannel, "Prerelease", StringComparison.OrdinalIgnoreCase);

    /// <summary>Per-channel storage subfolder so Dev / Prerelease / Release builds keep separate
    /// data (rules, settings, logs) and never clobber each other: "Dev" / "Prerelease" for the
    /// non-stable channels, empty for a stable Release build (which sits at the base folder).</summary>
    public static string ChannelFolder => BuildChannel switch
    {
        "Dev" => "Dev",
        "Prerelease" => "Prerelease",
        _ => string.Empty,
    };

    /// <summary>Version with a channel tag for non-stable builds, e.g. "0.1.8 (Dev)".</summary>
    public static string DisplayVersion => BuildChannel switch
    {
        "Dev" => $"{Version} (Dev)",
        "Prerelease" => $"{Version} (Pre-release)",
        _ => Version,
    };

    /// <summary>Short commit for display (first 7 chars), or null.</summary>
    public static string? ShortCommit =>
        string.IsNullOrEmpty(CommitHash) ? null : CommitHash[..Math.Min(7, CommitHash.Length)];

    public static string RepoUrl => $"https://github.com/{Owner}/{Repo}";
    public static string IssuesUrl => $"{RepoUrl}/issues";
    public static string ReleasesPageUrl => $"{RepoUrl}/releases/latest";
    public static string NewBugUrl => $"{RepoUrl}/issues/new?template=bug_report.yml";
    public static string NewFeatureUrl => $"{RepoUrl}/issues/new?template=feature_request.yml";

    /// <summary>
    /// Releases list (newest first). We read the list rather than <c>releases/latest</c> so a
    /// pre-release build can find the newest pre-release; stable builds just filter prereleases out.
    /// </summary>
    public static string ReleasesApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=30";

    /// <summary>
    /// True when running as a packaged (MSIX) app. Such builds update through the Microsoft Store,
    /// so the in-app update check is hidden for them. Resolved once via the Win32 packaging API
    /// (returns <c>APPMODEL_ERROR_NO_PACKAGE</c> when unpackaged) rather than catching an exception.
    /// </summary>
    public static bool IsPackaged { get; } = ResolveIsPackaged();

    private const int AppModelErrorNoPackage = 15700;

    // Querying with a null buffer returns APPMODEL_ERROR_NO_PACKAGE when unpackaged, or
    // ERROR_INSUFFICIENT_BUFFER when packaged - either way the return code alone tells us which.
    [DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, IntPtr packageFullName);

    private static bool ResolveIsPackaged()
    {
        try
        {
            var length = 0;
            return GetCurrentPackageFullName(ref length, IntPtr.Zero) != AppModelErrorNoPackage;
        }
        catch
        {
            return false;
        }
    }
}
