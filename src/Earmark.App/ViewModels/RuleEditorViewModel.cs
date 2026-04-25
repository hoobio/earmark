using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Services;

namespace Earmark.App.ViewModels;

public partial class RuleEditorViewModel : ObservableObject
{
    private readonly IRulesService _rules;
    private readonly IAudioEndpointService _endpoints;
    private readonly IAudioSessionService _sessions;
    private RoutingRule _model;

    public RuleEditorViewModel(IRulesService rules, IAudioEndpointService endpoints, IAudioSessionService sessions)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
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
    [NotifyPropertyChangedFor(nameof(IsAppValid))]
    public partial string AppPattern { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeviceValid))]
    public partial string DevicePattern { get; set; }

    [ObservableProperty]
    public partial AppMatchTarget AppTarget { get; set; }

    [ObservableProperty]
    public partial RoleScope Role { get; set; }

    [ObservableProperty]
    public partial EndpointFlow Flow { get; set; }

    [ObservableProperty]
    public partial string? AppPatternError { get; set; }

    [ObservableProperty]
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

        Name = _model.Name;
        Enabled = _model.Enabled;
        Priority = _model.Priority;
        AppPattern = _model.AppPattern;
        DevicePattern = _model.DevicePattern;
        AppTarget = _model.AppMatchTarget;
        Role = _model.Role;
        Flow = _model.Flow;
        RefreshPreviews();
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

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var rule = ToRule();
        await _rules.UpsertAsync(rule);
    }

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

    partial void OnFlowChanged(EndpointFlow value) => RefreshDeviceMatches();

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

        foreach (var session in _sessions.GetSessions())
        {
            var input = AppTarget == AppMatchTarget.ExecutablePath ? session.ExecutablePath : session.ProcessName;
            if (!string.IsNullOrEmpty(input) && regex.IsMatch(input))
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

        foreach (var endpoint in _endpoints.GetEndpoints(Flow))
        {
            if (regex.IsMatch(endpoint.FriendlyName) || regex.IsMatch(endpoint.DisplayName))
            {
                DeviceMatches.Add(new MatchPreviewItem(endpoint.FriendlyName, endpoint.DisplayName));
            }
        }
    }
}

public sealed record MatchPreviewItem(string Primary, string Secondary);
