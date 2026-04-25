using System.Text.RegularExpressions;

using CommunityToolkit.Mvvm.ComponentModel;

using Earmark.Core.Audio;
using Earmark.Core.Models;

namespace Earmark.App.ViewModels;

public partial class RuleRow : ObservableObject
{
    private static readonly RegexOptions Options =
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    public RuleRow(RoutingRule rule)
    {
        Rule = rule;
    }

    public RoutingRule Rule { get; }
    public Guid Id => Rule.Id;

    [ObservableProperty]
    public partial int AppMatchCount { get; set; }

    [ObservableProperty]
    public partial string AppMatchNames { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeviceMatchSummary { get; set; } = string.Empty;

    public bool HasAppMatches => AppMatchCount > 0;
    public bool HasDeviceMatch => !string.IsNullOrEmpty(DeviceMatchSummary);
    public string AppMatchSummary => AppMatchCount == 1
        ? "1 matching application"
        : $"{AppMatchCount} matching applications";

    partial void OnAppMatchCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasAppMatches));
        OnPropertyChanged(nameof(AppMatchSummary));
    }

    partial void OnDeviceMatchSummaryChanged(string value) =>
        OnPropertyChanged(nameof(HasDeviceMatch));

    public void Recompute(IReadOnlyList<AudioSession> sessions, IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (!Rule.IsValid)
        {
            AppMatchCount = 0;
            AppMatchNames = string.Empty;
            DeviceMatchSummary = string.Empty;
            return;
        }

        if (TryCompile(Rule.AppPattern, out var appRegex) && appRegex is not null)
        {
            var seenPids = new HashSet<uint>();
            var names = new List<string>();
            foreach (var session in sessions)
            {
                var input = Rule.AppMatchTarget == AppMatchTarget.ExecutablePath
                    ? session.ExecutablePath
                    : session.ProcessName;
                if (string.IsNullOrEmpty(input))
                {
                    continue;
                }

                try
                {
                    if (!appRegex.IsMatch(input))
                    {
                        continue;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    continue;
                }

                if (!seenPids.Add(session.ProcessId))
                {
                    continue;
                }

                names.Add(string.IsNullOrEmpty(session.ProcessName)
                    ? session.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : session.ProcessName);
            }

            AppMatchCount = names.Count;
            AppMatchNames = names.Count == 0 ? string.Empty : string.Join("\n", names);
        }
        else
        {
            AppMatchCount = 0;
            AppMatchNames = string.Empty;
        }

        if (TryCompile(Rule.DevicePattern, out var deviceRegex) && deviceRegex is not null)
        {
            var matched = endpoints
                .Where(e => e.Flow == Rule.Flow && e.State == EndpointState.Active)
                .Where(e =>
                {
                    try
                    {
                        return deviceRegex.IsMatch(e.FriendlyName) || deviceRegex.IsMatch(e.DisplayName);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return false;
                    }
                })
                .Select(e => e.FriendlyName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            DeviceMatchSummary = matched.Count switch
            {
                0 => string.Empty,
                1 => matched[0],
                _ => $"{matched[0]} (+{matched.Count - 1})",
            };
        }
        else
        {
            DeviceMatchSummary = string.Empty;
        }
    }

    private static bool TryCompile(string pattern, out Regex? regex)
    {
        regex = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        try
        {
            regex = new Regex(pattern, Options, TimeSpan.FromMilliseconds(250));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
