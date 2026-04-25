using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

using CommunityToolkit.Mvvm.ComponentModel;

using Earmark.App.Services;
using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Services;

namespace Earmark.App.ViewModels;

public partial class RuleEditorViewModel : ObservableObject
{
    private readonly IRulesService _rules;
    private readonly IAudioEndpointService _endpoints;
    private readonly IAudioSessionService _sessions;
    private readonly IDispatcherQueueProvider _dispatcher;

    private RoutingRule _model;
    private List<AudioEndpoint> _endpointSnapshot = new();
    private List<AudioSession> _sessionSnapshot = new();

    public RuleEditorViewModel(
        IRulesService rules,
        IAudioEndpointService endpoints,
        IAudioSessionService sessions,
        IDispatcherQueueProvider dispatcher)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _model = new RoutingRule();
        AppMatchTargets = Enum.GetValues<AppMatchTarget>().ToArray();
        Roles = Enum.GetValues<RoleScope>().ToArray();
        Flows = Enum.GetValues<EndpointFlow>().ToArray();
        Name = string.Empty;
        Enabled = true;
        AppPattern = string.Empty;
        DevicePattern = string.Empty;
        AppTarget = AppMatchTarget.ProcessName;
        Role = RoleScope.All;
        Flow = EndpointFlow.Render;
    }

    public AppMatchTarget[] AppMatchTargets { get; }
    public RoleScope[] Roles { get; }
    public EndpointFlow[] Flows { get; }

    public ObservableCollection<MatchPreviewItem> AppMatches { get; } = new();
    public ObservableCollection<MatchPreviewItem> DeviceMatches { get; } = new();

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial int Priority { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppValid), nameof(CanSave))]
    public partial string AppPattern { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeviceValid), nameof(CanSave))]
    public partial string DevicePattern { get; set; }

    [ObservableProperty]
    public partial AppMatchTarget AppTarget { get; set; }

    [ObservableProperty]
    public partial RoleScope Role { get; set; }

    [ObservableProperty]
    public partial EndpointFlow Flow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppValid), nameof(CanSave))]
    public partial string? AppPatternError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeviceValid), nameof(CanSave))]
    public partial string? DevicePatternError { get; set; }

    public bool IsAppValid => string.IsNullOrEmpty(AppPatternError);
    public bool IsDeviceValid => string.IsNullOrEmpty(DevicePatternError);

    public Guid Id => _model.Id;

    public bool CanSave =>
        !string.IsNullOrWhiteSpace(AppPattern) &&
        !string.IsNullOrWhiteSpace(DevicePattern) &&
        IsAppValid &&
        IsDeviceValid;

    public void Load(RoutingRule? rule)
    {
        _model = rule is null
            ? new RoutingRule()
            : new RoutingRule
            {
                Id = rule.Id,
                Name = rule.Name,
                Enabled = rule.Enabled,
                Priority = rule.Priority,
                AppPattern = rule.AppPattern,
                DevicePattern = rule.DevicePattern,
                AppMatchTarget = rule.AppMatchTarget,
                Role = rule.Role,
                Flow = rule.Flow,
            };

        _sessionSnapshot = new();
        _endpointSnapshot = new();

        Name = _model.Name;
        Enabled = _model.Enabled;
        Priority = _model.Priority;
        AppPattern = _model.AppPattern;
        DevicePattern = _model.DevicePattern;
        AppTarget = _model.AppMatchTarget;
        Role = _model.Role;
        Flow = _model.Flow;

        _ = LoadSnapshotsAsync(_model.Flow);
    }

    private async Task LoadSnapshotsAsync(EndpointFlow flow)
    {
        var (sessions, endpoints) = await Task.Run(() =>
        {
            var s = _sessions.GetSessions().ToList();
            var e = _endpoints.GetEndpoints(flow)
                .Where(x => x.State == EndpointState.Active)
                .ToList();
            return (s, e);
        }).ConfigureAwait(false);

        _dispatcher.Enqueue(() =>
        {
            _sessionSnapshot = sessions;
            _endpointSnapshot = endpoints;
            RefreshPreviews();
        });
    }

    public RoutingRule ToRule() =>
        new()
        {
            Id = _model.Id,
            Name = string.IsNullOrWhiteSpace(Name) ? AppPattern : Name,
            Enabled = Enabled,
            Priority = Priority,
            AppPattern = AppPattern,
            DevicePattern = DevicePattern,
            AppMatchTarget = AppTarget,
            Role = Role,
            Flow = Flow,
        };

    public Task SaveAsync() => _rules.UpsertAsync(ToRule());

    partial void OnAppPatternChanged(string value)
    {
        AppPatternError = ValidateRegex(value);
        RefreshAppMatches();
    }

    partial void OnDevicePatternChanged(string value)
    {
        DevicePatternError = ValidateRegex(value);
        RefreshDeviceMatches();
    }

    partial void OnAppTargetChanged(AppMatchTarget value) => RefreshAppMatches();

    partial void OnFlowChanged(EndpointFlow value) => _ = ReloadEndpointsAsync(value);

    private async Task ReloadEndpointsAsync(EndpointFlow flow)
    {
        var endpoints = await Task.Run(() =>
            _endpoints.GetEndpoints(flow).Where(e => e.State == EndpointState.Active).ToList())
            .ConfigureAwait(false);

        _dispatcher.Enqueue(() =>
        {
            _endpointSnapshot = endpoints;
            RefreshDeviceMatches();
        });
    }

    private static string? ValidateRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250));
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    private void RefreshPreviews()
    {
        RefreshAppMatches();
        RefreshDeviceMatches();
    }

    private void RefreshAppMatches()
    {
        AppMatches.Clear();
        if (string.IsNullOrWhiteSpace(AppPattern) || !IsAppValid)
        {
            return;
        }

        Regex regex;
        try
        {
            regex = new Regex(AppPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in _sessionSnapshot)
        {
            var input = AppTarget == AppMatchTarget.ExecutablePath ? session.ExecutablePath : session.ProcessName;
            if (string.IsNullOrEmpty(input) || !regex.IsMatch(input))
            {
                continue;
            }

            var key = $"{input}\0{session.DisplayName}";
            if (seen.Add(key))
            {
                AppMatches.Add(new MatchPreviewItem(input, session.DisplayName));
            }
        }
    }

    private void RefreshDeviceMatches()
    {
        DeviceMatches.Clear();
        if (string.IsNullOrWhiteSpace(DevicePattern) || !IsDeviceValid)
        {
            return;
        }

        Regex regex;
        try
        {
            regex = new Regex(DevicePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            return;
        }

        var groups = _endpointSnapshot
            .Where(e => regex.IsMatch(e.FriendlyName) || regex.IsMatch(e.DisplayName))
            .GroupBy(e => $"{e.FriendlyName}\0{e.DeviceDescription}", StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var first = group.First();
            var count = group.Count();
            var description = string.IsNullOrEmpty(first.DeviceDescription) ? "endpoint" : first.DeviceDescription;
            var secondary = count > 1 ? $"{description} ({count} endpoints match)" : description;
            DeviceMatches.Add(new MatchPreviewItem(first.FriendlyName, secondary));
        }
    }
}

public sealed record MatchPreviewItem(string Primary, string Secondary);
