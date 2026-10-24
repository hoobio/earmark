using System.ComponentModel;
using System.Runtime.CompilerServices;

using Earmark.Core.Models;
using Earmark.Core.Routing;
using Earmark.Core.WaveLink;

namespace Earmark.Desktop.ViewModels.Rules;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void Raise(params string[] names)
    {
        foreach (var name in names)
        {
            Raise(name);
        }
    }
}

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class PatternField : Observable
{
    private readonly Func<string> _getPattern;
    private readonly Action<string> _setPattern;
    private readonly Func<PatternMatchMode> _getMode;
    private readonly Action<PatternMatchMode> _setMode;
    private readonly Action _changed;

    public PatternField(string label, string placeholder, Func<string> getPattern, Action<string> setPattern, Func<PatternMatchMode> getMode, Action<PatternMatchMode> setMode, Action changed)
    {
        Label = label;
        Placeholder = placeholder;
        Glyph = label switch { "Device" => "", "App" => "", _ => "" };
        PickPlaceholder = $"Pick {(label == "App" ? "an" : "a")} {label.ToLowerInvariant()}";
        ModeOptions = [new(PatternMatchMode.Regex, "Regex"), new(PatternMatchMode.Wildcard, "Wildcard"), new(PatternMatchMode.Exact, label)];
        (_getPattern, _setPattern, _getMode, _setMode, _changed) = (getPattern, setPattern, getMode, setMode, changed);
    }

    public string Label { get; }

    public string Placeholder { get; }

    public string PickPlaceholder { get; }

    public string Glyph { get; }

    public IReadOnlyList<Choice<PatternMatchMode>> ModeOptions { get; }

    public string Pattern
    {
        get => _getPattern();
        set
        {
            if (value is null || value == _getPattern()) return;
            _setPattern(value);
            Raise();
            _changed();
        }
    }

    public Choice<PatternMatchMode> Mode
    {
        get => ModeOptions.First(o => o.Value == _getMode());
        set
        {
            if (value is null || value.Value == _getMode()) return;
            _setMode(value.Value);
            Raise(nameof(Mode), nameof(IsPick), nameof(IsText));
            _changed();
        }
    }

    public bool IsPick => _getMode() == PatternMatchMode.Exact;

    public bool IsText => !IsPick;

    public bool IsRegexValid => _getMode() != PatternMatchMode.Regex || string.IsNullOrWhiteSpace(_getPattern()) || RegexCache.TryGet(_getPattern(), out _);

    public IReadOnlyList<string> Candidates { get; private set; } = [];

    public string Summary { get; private set; } = string.Empty;

    public bool HasSummary => Summary.Length > 0;

    public string? SummaryTip { get; private set; }

    public string OfflineNames { get; private set; } = string.Empty;

    public bool IsOffline => OfflineNames.Length > 0;

    internal void SetPreview(string summary, string? tip, string offlineNames, IReadOnlyList<string> candidates)
    {
        (Summary, SummaryTip, OfflineNames, Candidates) = (summary, tip, offlineNames, candidates);
        Raise(nameof(Summary), nameof(HasSummary), nameof(SummaryTip), nameof(OfflineNames), nameof(IsOffline), nameof(Candidates));
    }

    internal static IReadOnlyList<string> Sorted(IEnumerable<string> names) =>
        names.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static string Summarise(IReadOnlyList<string> names) =>
        names.Count switch { 0 => string.Empty, 1 => names[0], _ => $"{names[0]} (+{names.Count - 1})" };

    internal void PreviewDevices(IReadOnlyList<AudioEndpoint> endpoints, Func<AudioEndpoint, bool> filter)
    {
        var pattern = _getPattern();
        var mode = _getMode();
        var matched = string.IsNullOrWhiteSpace(pattern)
            ? []
            : endpoints.Where(filter)
                .Where(e => PatternMatcher.Matches(mode, pattern, e.FriendlyName) || PatternMatcher.Matches(mode, pattern, e.DisplayName))
                .GroupBy(e => e.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key, Connected: g.Any(e => e.State == EndpointState.Active)))
                .OrderByDescending(m => m.Connected)
                .ToList();
        ConnectedCount = matched.Count(m => m.Connected);
        SetPreview(
            Summarise(matched.Select(m => m.Name).ToList()),
            null,
            string.Join("\n", matched.Where(m => !m.Connected).Select(m => m.Name)),
            Sorted(endpoints.Where(e => e.State == EndpointState.Active && filter(e)).Select(e => e.PickerName)));
    }

    internal int ConnectedCount { get; private set; }

    internal int PreviewApps(IReadOnlyList<AudioSession> sessions)
    {
        var pattern = _getPattern();
        var mode = _getMode();
        var names = string.IsNullOrWhiteSpace(pattern)
            ? []
            : sessions.Where(s => PatternMatcher.Matches(mode, pattern, s.ProcessName) || PatternMatcher.Matches(mode, pattern, s.ExecutablePath))
                .DistinctBy(s => s.IdentityKey)
                .Select(s => string.IsNullOrEmpty(s.ProcessName) ? s.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) : s.ProcessName)
                .ToList();
        SetPreview(
            names.Count switch { 0 => string.Empty, 1 => "1 matching app", _ => $"{names.Count} matching apps" },
            string.Join("\n", names),
            string.Empty,
            Sorted(sessions.Select(s => s.ProcessName)));
        return names.Count;
    }

    internal void PreviewNames(IEnumerable<string> available)
    {
        var pattern = _getPattern();
        var mode = _getMode();
        var all = Sorted(available);
        SetPreview(string.IsNullOrWhiteSpace(pattern) ? string.Empty : Summarise(all.Where(n => PatternMatcher.Matches(mode, pattern, n)).ToList()), null, string.Empty, all);
    }
}

public sealed class ConditionRow : Observable
{
    private static readonly IReadOnlyList<Choice<ConditionKind>> Kinds =
        [new(ConditionKind.Device, "Device"), new(ConditionKind.DefaultDevice, "Default device"), new(ConditionKind.Application, "Application")];

    private static readonly IReadOnlyList<Choice<ConditionFlow>> Flows =
        [new(ConditionFlow.Any, "Any"), new(ConditionFlow.Render, "Output"), new(ConditionFlow.Capture, "Input")];

    private readonly RuleCondition _model;

    public ConditionRow(RuleRow owner, RuleCondition model)
    {
        Owner = owner;
        _model = model.Clone();
        Device = new PatternField("Device", ".*Headphones.*", () => _model.DevicePattern, v => _model.DevicePattern = v, () => _model.DeviceMatchMode, v => _model.DeviceMatchMode = v, Changed);
        App = new PatternField("App", ".*discord.*", () => _model.AppPattern, v => _model.AppPattern = v, () => _model.AppMatchMode, v => _model.AppMatchMode = v, Changed);
    }

    public RuleRow Owner { get; }

    public PatternField Device { get; }

    public PatternField App { get; }

    public IReadOnlyList<Choice<ConditionKind>> KindOptions { get; } = Kinds;

    public IReadOnlyList<Choice<ConditionFlow>> FlowOptions { get; } = Flows;

    public Choice<ConditionKind> Kind
    {
        get => Kinds.First(o => o.Value == _model.Kind);
        set
        {
            if (value is null || value.Value == _model.Kind) return;
            _model.Kind = value.Value;
            Raise(nameof(Kind), nameof(IsApplication), nameof(IsDevice), nameof(PositiveLabel), nameof(NegativeLabel));
            Changed();
        }
    }

    public Choice<ConditionFlow> Flow
    {
        get => Flows.First(o => o.Value == _model.Flow);
        set
        {
            if (value is null || value.Value == _model.Flow) return;
            _model.Flow = value.Value;
            Raise();
            Changed();
        }
    }

    public bool IsPositive
    {
        get => !_model.Negate;
        set
        {
            if (value != _model.Negate) return;
            _model.Negate = !value;
            Raise();
            Changed();
        }
    }

    public bool IsApplication => _model.Kind == ConditionKind.Application;

    public bool IsDevice => !IsApplication;

    public string PositiveLabel => _model.Kind switch { ConditionKind.Application => "Running", ConditionKind.DefaultDevice => "Is default", _ => "Present" };

    public string NegativeLabel => _model.Kind switch { ConditionKind.Application => "Not running", ConditionKind.DefaultDevice => "Not default", _ => "Missing" };

    public bool IsSatisfied { get; private set; }

    public string SatisfiedTip => IsSatisfied ? "This condition is true right now" : "This condition is false right now";

    public RuleCondition ToCondition() => _model.Clone();

    internal void Preview(IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<AudioSession> sessions)
    {
        bool positive;
        if (IsApplication)
        {
            positive = App.PreviewApps(sessions) > 0;
        }
        else
        {
            var requireDefault = _model.Kind == ConditionKind.DefaultDevice;
            Device.PreviewDevices(endpoints, e => FlowMatches(e.Flow) && (!requireDefault || e.IsDefault || e.IsDefaultCommunications));
            positive = Device.ConnectedCount > 0;
        }

        IsSatisfied = positive != _model.Negate;
        Raise(nameof(IsSatisfied), nameof(SatisfiedTip));
    }

    private bool FlowMatches(EndpointFlow flow) => _model.Flow switch
    {
        ConditionFlow.Render => flow == EndpointFlow.Render,
        ConditionFlow.Capture => flow == EndpointFlow.Capture,
        _ => true,
    };

    private void Changed() => Owner.ChildChanged();
}

public sealed class ActionRow : Observable
{
    private static readonly IReadOnlyList<Choice<ActionKind>> Kinds =
    [
        new(ActionKind.ApplicationDevice, "Set device for app"),
        new(ActionKind.DefaultDevice, "Set system default device"),
        new(ActionKind.WaveLinkMix, "Wave Link mix"),
        new(ActionKind.DeviceVolume, "Set device volume"),
        new(ActionKind.DeviceMute, "Mute device"),
    ];

    // RenameDevice is parked (needs an elevated registry write), so only a rule that already holds one lists it.
    private static readonly IReadOnlyList<Choice<ActionKind>> KindsWithRename = [.. Kinds, new(ActionKind.RenameDevice, "Rename device")];

    private static readonly IReadOnlyList<Choice<MixMembership>> Memberships =
    [
        new(MixMembership.Include, "Add to / keep in mix"),
        new(MixMembership.Exclude, "Remove from / keep out of mix"),
        new(MixMembership.Exclusive, "Set as mix's only outputs"),
    ];

    private readonly RuleAction _model;
    private readonly IReadOnlyList<Choice<ActionKind>> _kinds;

    public ActionRow(RuleRow owner, RuleAction model)
    {
        Owner = owner;
        _model = model.Clone();
        _kinds = _model.Kind == ActionKind.RenameDevice ? KindsWithRename : Kinds;
        App = new PatternField("App", ".*chrome.*", () => _model.AppPattern, v => _model.AppPattern = v, () => _model.AppMatchMode, v => _model.AppMatchMode = v, Changed);
        Device = new PatternField("Device", ".*Headphones.*", () => _model.DevicePattern, v => _model.DevicePattern = v, () => _model.DeviceMatchMode, v => _model.DeviceMatchMode = v, Changed);
        Mix = new PatternField("Mix", "Headphone Mix", () => _model.MixPattern, v => _model.MixPattern = v, () => _model.MixMatchMode, v => _model.MixMatchMode = v, Changed);
    }

    public RuleRow Owner { get; }

    public bool IsElse => Owner.ElseActions.Contains(this);

    public PatternField App { get; }

    public PatternField Device { get; }

    public PatternField Mix { get; }

    public IReadOnlyList<Choice<ActionKind>> KindOptions => _kinds;

    public IReadOnlyList<Choice<MixMembership>> MembershipOptions { get; } = Memberships;

    public Choice<ActionKind> Kind
    {
        get => _kinds.First(o => o.Value == _model.Kind);
        set
        {
            if (value is null || value.Value == _model.Kind) return;
            _model.Kind = value.Value;
            Raise(nameof(Kind), nameof(IsApp), nameof(IsDefault), nameof(IsWaveLink), nameof(IsVolume), nameof(IsMute), nameof(IsRename), nameof(ShowDirection));
            Changed();
        }
    }

    public Choice<MixMembership> Membership
    {
        get => Memberships.First(o => o.Value == _model.Membership);
        set
        {
            if (value is null || value.Value == _model.Membership) return;
            _model.Membership = value.Value;
            Raise();
            Changed();
        }
    }

    public bool IsInput
    {
        get => _model.Flow == EndpointFlow.Capture;
        set => Set(value == IsInput, () => _model.Flow = value ? EndpointFlow.Capture : EndpointFlow.Render);
    }

    public bool Muted
    {
        get => _model.Muted;
        set => Set(value == _model.Muted, () => _model.Muted = value);
    }

    public bool Pinned
    {
        get => _model.Pinned;
        set => Set(value == _model.Pinned, () => _model.Pinned = value);
    }

    public bool SetsDefault
    {
        get => _model.SetsDefault;
        set => Set(value == _model.SetsDefault, () => _model.SetsDefault = value);
    }

    public bool SetsCommunications
    {
        get => _model.SetsCommunications;
        set => Set(value == _model.SetsCommunications, () => _model.SetsCommunications = value);
    }

    public double VolumePercent
    {
        get => Math.Round(_model.Volume * 100);
        set => Set(Math.Abs(value - VolumePercent) < 0.5, () => _model.Volume = (float)Math.Clamp(value / 100, 0, 1));
    }

    public string NewName
    {
        get => _model.NewName;
        set => Set(value is null || value == _model.NewName, () => _model.NewName = value!);
    }

    public bool IsApp => _model.Kind == ActionKind.ApplicationDevice;

    public bool IsDefault => _model.Kind == ActionKind.DefaultDevice;

    public bool IsWaveLink => _model.Kind == ActionKind.WaveLinkMix;

    public bool IsVolume => _model.Kind == ActionKind.DeviceVolume;

    public bool IsMute => _model.Kind == ActionKind.DeviceMute;

    public bool IsRename => _model.Kind == ActionKind.RenameDevice;

    public bool ShowDirection => IsApp || IsDefault;

    public bool IsShadowed { get; private set; }

    public string Diagnostic { get; private set; } = string.Empty;

    public bool HasDiagnostic => Diagnostic.Length > 0;

    public string TypeLabel => _model.Kind switch
    {
        ActionKind.ApplicationDevice => IsInput ? "App input" : "App output",
        ActionKind.DefaultDevice => IsInput ? "Default input" : "Default output",
        ActionKind.WaveLinkMix => _model.Membership switch { MixMembership.Exclude => "Remove from mix", MixMembership.Exclusive => "Set mix outputs", _ => "Add to mix" },
        ActionKind.DeviceVolume => "Set volume",
        ActionKind.DeviceMute => Muted ? "Mute" : "Unmute",
        _ => "Rename",
    };

    internal int AppMatchCount { get; private set; }

    public RuleAction ToAction() => _model.Clone();

    internal void SetShadowed(bool shadowed)
    {
        IsShadowed = shadowed;
        Raise(nameof(IsShadowed));
    }

    internal void Preview(IReadOnlyList<AudioSession> sessions, IReadOnlyList<AudioEndpoint> endpoints, WaveLinkSnapshot? waveLink, WaveLinkConnectionState waveLinkState)
    {
        AppMatchCount = IsApp ? App.PreviewApps(sessions) : 0;
        if (IsWaveLink)
        {
            Device.PreviewNames(waveLink?.OutputDevices.Select(o => o.DeviceName) ?? []);
            Mix.PreviewNames(waveLink?.Mixes.Select(m => m.Name) ?? []);
            Diagnostic = WaveLinkDiagnostic(waveLinkState, waveLink);
        }
        else
        {
            var flow = ShowDirection ? _model.Flow : (EndpointFlow?)null;
            Device.PreviewDevices(endpoints, e => flow is null || e.Flow == flow);
            Diagnostic = (IsApp && !string.IsNullOrWhiteSpace(_model.AppPattern) && !App.IsRegexValid) ? $"App pattern '{_model.AppPattern}' is not valid regex"
                : !string.IsNullOrWhiteSpace(_model.DevicePattern) && !Device.IsRegexValid ? $"Device pattern '{_model.DevicePattern}' is not valid regex"
                : IsRename && !string.IsNullOrWhiteSpace(_model.DevicePattern) && string.IsNullOrWhiteSpace(_model.NewName) ? "Enter the new device name to apply"
                : string.Empty;
        }

        Raise(nameof(Diagnostic), nameof(HasDiagnostic));
    }

    private string WaveLinkDiagnostic(WaveLinkConnectionState state, WaveLinkSnapshot? snapshot)
    {
        static string List(IEnumerable<string> names, string none) => names.Any() ? string.Join(", ", names.Select(n => $"'{n}'")) : none;

        return state == WaveLinkConnectionState.Disabled ? "Wave Link integration is off in Settings"
            : state == WaveLinkConnectionState.Unavailable || snapshot is null ? "Wave Link not connected"
            : string.IsNullOrWhiteSpace(_model.MixPattern) ? "Mix pattern is empty"
            : string.IsNullOrWhiteSpace(_model.DevicePattern) ? "Device pattern is empty"
            : !Mix.IsRegexValid ? $"Mix pattern '{_model.MixPattern}' is not valid regex"
            : !Device.IsRegexValid ? $"Device pattern '{_model.DevicePattern}' is not valid regex"
            : !Mix.HasSummary ? $"Mix pattern '{_model.MixPattern}' does not match any current mix. Available: {List(snapshot.Mixes.Select(m => m.Name), "(no mixes)")}"
            : !Device.HasSummary ? $"Device pattern '{_model.DevicePattern}' does not match any current output device. Available: {List(snapshot.OutputDevices.Select(d => d.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase), "(no devices)")}"
            : string.Empty;
    }

    private void Set(bool unchanged, Action apply, [CallerMemberName] string? name = null)
    {
        if (unchanged) return;
        apply();
        Raise(name);
        Raise(nameof(TypeLabel));
        Changed();
    }

    private void Changed() => Owner.ChildChanged();
}
