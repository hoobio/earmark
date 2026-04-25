using System.Text.RegularExpressions;

using CommunityToolkit.Mvvm.ComponentModel;

using Earmark.Core.Audio;
using Earmark.Core.Models;

namespace Earmark.App.ViewModels;

public partial class RuleRow : ObservableObject, IDisposable
{
    public void CancelPendingSave()
    {
        lock (_saveGate)
        {
            _saveCts?.Cancel();
        }
    }

    public void Dispose()
    {
        CancelPendingSave();
        _saveCts?.Dispose();
        _disposed = true;
    }

    private static readonly RegexOptions Options =
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);

    private readonly Func<RoutingRule, Task> _persistAsync;
    private readonly Lock _saveGate = new();
    private CancellationTokenSource? _saveCts;
    private bool _suppress;
    private bool _disposed;

    public RuleRow(RoutingRule rule, Func<RoutingRule, Task> persistAsync)
    {
        Id = rule.Id;
        _persistAsync = persistAsync;
        SyncFromRule(rule);
    }

    public Guid Id { get; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial RuleType Type { get; set; }

    [ObservableProperty]
    public partial string AppPattern { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DevicePattern { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SetsDefault { get; set; }

    [ObservableProperty]
    public partial bool SetsCommunications { get; set; }

    [ObservableProperty]
    public partial int AppMatchCount { get; set; }

    [ObservableProperty]
    public partial string AppMatchNames { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeviceMatchSummary { get; set; } = string.Empty;

    public bool RequiresAppPattern => Type is RuleType.ApplicationOutput or RuleType.ApplicationInput;
    public bool IsDefaultRule => Type is RuleType.DefaultOutput or RuleType.DefaultInput;
    public bool HasAppMatches => AppMatchCount > 0;
    public bool HasDeviceMatch => !string.IsNullOrEmpty(DeviceMatchSummary);

    public static IReadOnlyList<RuleTypeOption> TypeOptions { get; } = new[]
    {
        new RuleTypeOption(RuleType.ApplicationOutput, "Application -> output device"),
        new RuleTypeOption(RuleType.ApplicationInput, "Application -> input device"),
        new RuleTypeOption(RuleType.DefaultOutput, "Default output device"),
        new RuleTypeOption(RuleType.DefaultInput, "Default input device"),
    };

    public string TypeLabel => Type switch
    {
        RuleType.ApplicationOutput => "App output",
        RuleType.ApplicationInput => "App input",
        RuleType.DefaultOutput => "Default output",
        RuleType.DefaultInput => "Default input",
        _ => Type.ToString(),
    };

    public RuleTypeOption SelectedTypeOption
    {
        get => TypeOptions.FirstOrDefault(o => o.Value == Type) ?? TypeOptions[0];
        set
        {
            if (value is not null && Type != value.Value)
            {
                Type = value.Value;
            }
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? (RequiresAppPattern && !string.IsNullOrWhiteSpace(AppPattern) ? AppPattern : TypeLabel)
        : Name;

    public string AppMatchSummary => AppMatchCount == 1
        ? "1 matching application"
        : $"{AppMatchCount} matching applications";

#pragma warning disable CA1822
    public IReadOnlyList<RuleTypeOption> AvailableTypeOptions => TypeOptions;
#pragma warning restore CA1822

    public RoutingRule ToRule() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        Type = Type,
        AppPattern = AppPattern,
        DevicePattern = DevicePattern,
        SetsDefault = SetsDefault,
        SetsCommunications = SetsCommunications,
    };

    public void SyncFromRule(RoutingRule rule)
    {
        _suppress = true;
        try
        {
            Name = rule.Name;
            Enabled = rule.Enabled;
            Type = rule.Type;
            AppPattern = rule.AppPattern;
            DevicePattern = rule.DevicePattern;
            SetsDefault = rule.SetsDefault;
            SetsCommunications = rule.SetsCommunications;
        }
        finally
        {
            _suppress = false;
        }
    }

    public void Recompute(IReadOnlyList<AudioSession> sessions, IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (RequiresAppPattern)
        {
            RecomputeApp(sessions);
        }
        else
        {
            AppMatchCount = 0;
            AppMatchNames = string.Empty;
        }

        DeviceMatchSummary = ResolveDeviceName(DevicePattern, EffectiveFlow(Type), endpoints);
    }

    private void RecomputeApp(IReadOnlyList<AudioSession> sessions)
    {
        if (!TryCompile(AppPattern, out var regex) || regex is null)
        {
            AppMatchCount = 0;
            AppMatchNames = string.Empty;
            return;
        }

        var seen = new HashSet<uint>();
        var names = new List<string>();
        foreach (var session in sessions)
        {
            if (!Match(regex, session.ProcessName) && !Match(regex, session.ExecutablePath))
            {
                continue;
            }

            if (!seen.Add(session.ProcessId))
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

    private static bool Match(Regex regex, string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static EndpointFlow EffectiveFlow(RuleType type) => type switch
    {
        RuleType.ApplicationInput or RuleType.DefaultInput => EndpointFlow.Capture,
        _ => EndpointFlow.Render,
    };

    private static string ResolveDeviceName(string pattern, EndpointFlow flow, IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return string.Empty;
        }

        if (!TryCompile(pattern, out var regex) || regex is null)
        {
            return string.Empty;
        }

        var matched = endpoints
            .Where(e => e.Flow == flow && e.State == EndpointState.Active)
            .Where(e =>
            {
                try
                {
                    return regex.IsMatch(e.FriendlyName) || regex.IsMatch(e.DisplayName);
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            })
            .Select(e => e.FriendlyName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return matched.Count switch
        {
            0 => string.Empty,
            1 => matched[0],
            _ => $"{matched[0]} (+{matched.Count - 1})",
        };
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

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        QueueSave();
    }
    partial void OnEnabledChanged(bool value) => QueueSave();
    partial void OnTypeChanged(RuleType value)
    {
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(RequiresAppPattern));
        OnPropertyChanged(nameof(IsDefaultRule));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(SelectedTypeOption));
        QueueSave();
    }
    partial void OnSetsDefaultChanged(bool value) => QueueSave();
    partial void OnSetsCommunicationsChanged(bool value) => QueueSave();
    partial void OnAppPatternChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        QueueSave();
    }
    partial void OnDevicePatternChanged(string value) => QueueSave();
    partial void OnAppMatchCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasAppMatches));
        OnPropertyChanged(nameof(AppMatchSummary));
    }
    partial void OnDeviceMatchSummaryChanged(string value) =>
        OnPropertyChanged(nameof(HasDeviceMatch));

    private void QueueSave()
    {
        if (_suppress || _disposed)
        {
            return;
        }

        CancellationToken token;
        lock (_saveGate)
        {
            if (_disposed)
            {
                return;
            }

            _saveCts?.Cancel();
            _saveCts = new CancellationTokenSource();
            token = _saveCts.Token;
        }

        _ = SaveAsync(token);
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SaveDebounce, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_disposed || ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await _persistAsync(ToRule()).ConfigureAwait(false);
        }
        catch
        {
            // Persistence errors are surfaced via the unhandled-exception handler.
        }
    }
}

public sealed record RuleTypeOption(RuleType Value, string Label)
{
    public override string ToString() => Label;
}
