using System.Reflection;
using System.Runtime.InteropServices;

namespace Earmark.Desktop.Services;

internal static class AppInfo
{
    private const string Owner = "hoobio";
    private const string Repo = "earmark";
    private const int AppModelErrorNoPackage = 15700;

    static AppInfo()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var plusIndex = informational?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        var versionPart = plusIndex >= 0 ? informational![..plusIndex] : informational;
        CommitHash = plusIndex >= 0 ? informational![(plusIndex + 1)..] : null;
        Version = string.IsNullOrWhiteSpace(versionPart) ? assembly.GetName().Version?.ToString(3) ?? "0.0.0" : versionPart;
        BuildChannel = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildChannel")?.Value ?? "Dev";
        Copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "Copyright (c) Hoobi";
        DataDirectory = ChannelPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hoobi", "Earmark"));
        LogDirectory = Path.Combine(ChannelPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Earmark")), "logs");
        CurrentLogPath = Path.Combine(LogDirectory, $"earmark-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    public static string Version { get; }

    public static string? CommitHash { get; }

    public static string BuildChannel { get; }

    public static string Copyright { get; }

    public static bool IsDevBuild => string.Equals(BuildChannel, "Dev", StringComparison.OrdinalIgnoreCase);

    public static bool IsPrerelease => string.Equals(BuildChannel, "Prerelease", StringComparison.OrdinalIgnoreCase);

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

    public static string? ShortCommit => string.IsNullOrEmpty(CommitHash) ? null : CommitHash[..Math.Min(7, CommitHash.Length)];

    public static string RepoUrl => $"https://github.com/{Owner}/{Repo}";

    public static string IssuesUrl => $"{RepoUrl}/issues";

    public static string ReleasesPageUrl => $"{RepoUrl}/releases/latest";

    public static string NewBugUrl => $"{RepoUrl}/issues/new?template=bug_report.yml";

    public static string NewFeatureUrl => $"{RepoUrl}/issues/new?template=feature_request.yml";

    public static string DonateUrl => "https://www.paypal.com/donate/?hosted_button_id=SWG4G7VH3ZFQN";

    public static string ReleasesApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=30";

    public static bool IsPackaged { get; } = ResolveIsPackaged();

    public static string DataDirectory { get; }

    public static string LogDirectory { get; }

    public static string CurrentLogPath { get; }

    private static string ChannelPath(string baseDir) => ChannelFolder is { Length: > 0 } channel ? Path.Combine(baseDir, channel) : baseDir;

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, IntPtr packageFullName);

    private static bool ResolveIsPackaged()
    {
        var length = 0;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) != AppModelErrorNoPackage;
    }
}
