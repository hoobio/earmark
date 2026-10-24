using System.Reflection;

namespace Earmark.Core;

public static class AppInfo
{
    private const string Owner = "hoobio";
    private const string Repo = "earmark";

    static AppInfo()
    {
        // BuildChannel and the commit are stamped onto the head exe only, not onto this library.
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        var plusIndex = informational?.IndexOf('+') ?? -1;
        var versionPart = plusIndex >= 0 ? informational![..plusIndex] : informational;
        CommitHash = plusIndex >= 0 ? informational![(plusIndex + 1)..] : null;

        // Keeps the "-pre.N" suffix: the update check compares the full semver against newer pre-release tags.
        Version = string.IsNullOrWhiteSpace(versionPart)
            ? assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : versionPart;

        BuildChannel = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildChannel")?.Value ?? "Dev";

        Copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "Copyright (c) Hoobi";
    }

    public static string Version { get; }

    public static string? CommitHash { get; }

    public static string BuildChannel { get; }

    public static string Copyright { get; }

    public static bool IsDevBuild => string.Equals(BuildChannel, "Dev", StringComparison.OrdinalIgnoreCase);

    public static bool IsPrerelease => string.Equals(BuildChannel, "Prerelease", StringComparison.OrdinalIgnoreCase);

    public static bool IsRelease => string.Equals(BuildChannel, "Release", StringComparison.OrdinalIgnoreCase);

    public static string ChannelFolder => BuildChannel switch
    {
        "Dev" => "Dev",
        "Prerelease" => "Prerelease",
        _ => string.Empty,
    };

    public static string DisplayVersion => BuildChannel switch
    {
        "Dev" => $"{Version} (Dev)",
        "Prerelease" => $"{Version} (Pre-release)",
        _ => Version,
    };

    public static string? ShortCommit =>
        string.IsNullOrEmpty(CommitHash) ? null : CommitHash[..Math.Min(7, CommitHash.Length)];

    public static string RepoUrl => $"https://github.com/{Owner}/{Repo}";
    public static string IssuesUrl => $"{RepoUrl}/issues";
    public static string ReleasesPageUrl => $"{RepoUrl}/releases/latest";
    public static string NewBugUrl => $"{RepoUrl}/issues/new?template=bug_report.yml";
    public static string NewFeatureUrl => $"{RepoUrl}/issues/new?template=feature_request.yml";

    public static string DonateUrl => "https://www.paypal.com/donate/?hosted_button_id=SWG4G7VH3ZFQN";

    // The list, not releases/latest: a pre-release build needs the newest pre-release; stable builds filter them out.
    public static string ReleasesApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=30";
}
