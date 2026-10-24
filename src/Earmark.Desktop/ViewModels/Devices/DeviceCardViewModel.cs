using System.Collections.ObjectModel;

using Avalonia.Media;
using Avalonia.Media.Imaging;

using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Settings;

namespace Earmark.Desktop.ViewModels.Devices;

public sealed class DeviceCardViewModel : Observable
{
    private const double PeakHoldSeconds = 1.5;
    private const float PeakHoldDecayPerSecond = 0.55f;
    private static readonly TimeSpan UserVolumeGrace = TimeSpan.FromMilliseconds(600);
    private static readonly IBrush CriticalBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0x87, 0x79));
    private static readonly IBrush AccentTextBrush = new SolidColorBrush(Color.FromRgb(0x73, 0xD0, 0xFF));

    private readonly Color _autoAccent;
    private readonly DateTime[] _holdExpiry = new DateTime[3];
    private double _volume;
    private bool _isMuted;
    private bool _suppressWrite;
    private DateTime _lastUserVolume;
    private DeviceRuleState _ruleState = DeviceRuleState.Empty;
    private bool _isRulesExpanded;
    private bool _isGroupMember;
    private bool _isBluetoothBusy;
    private Color? _waveLinkAccent;
    private Bitmap? _waveLinkIcon;
    private string? _waveLinkGlyph;
    private string? _nameGlyph;

    public DeviceCardViewModel(DevicesViewModel owner, string key, AudioEndpoint endpoint, DeviceConfig config)
    {
        Owner = owner;
        Key = key;
        Endpoint = endpoint;
        Config = config;
        _autoAccent = DeviceVisuals.DeterministicSwatch(key);
        _nameGlyph = DeviceVisuals.GlyphForName(Endpoint.Name);
        PageOptions = CardOptions.For(owner.Settings, overlay: false, config);
        OverlayOptions = CardOptions.For(owner.Settings, overlay: true, config);
        Apps.CollectionChanged += (_, _) => RaiseApps();
    }

    public DevicesViewModel Owner { get; }

    public string Key { get; }

    public AudioEndpoint Endpoint { get; private set; }

    public DeviceConfig Config { get; private set; }

    public CardOptions PageOptions { get; }

    public CardOptions OverlayOptions { get; }

    public bool IsConnected { get; private set; } = true;

    public bool IsBluetooth { get; private set; }

    public ObservableCollection<AppChipViewModel> Apps { get; } = [];

    public ObservableCollection<NowPlayingViewModel> NowPlaying { get; } = [];

    public float LeftLevel { get; private set; }

    public float RightLevel { get; private set; }

    public float CentreLevel { get; private set; }

    public float Hold { get; private set; }

    public int ChannelCount { get; private set; } = 2;

    public bool IsRender => Endpoint.Flow == EndpointFlow.Render;

    public string Name => Endpoint.PickerName;

    public bool IsDefault => Endpoint.IsDefault;

    public bool IsDefaultCommunications => Endpoint.IsDefaultCommunications;

    public bool ShowFlowLabel => !IsDefault && !IsDefaultCommunications;

    public string FlowLabel => IsRender ? "Output" : "Input";

    public string DefaultPillText => IsRender ? "Default Output" : "Default Input";

    public string CommunicationsPillText => IsRender ? "Communications Output" : "Communications Input";

    public bool IsDisconnected => !IsConnected;

    public bool IsHidden => Config.Hidden == true;

    public bool IsQuickPinned => Config.PinnedToQuickControls == true;

    public bool ShowVolumeControls => Config.VolumeControlsHidden != true;

    public bool IsEffectivelyHidden =>
        Config.Hidden == true ? true
        : Config.PinnedToQuickControls == true || Config.Pinned == true || IsDefault || IsDefaultCommunications ? false
        : _ruleState.Rules.Count == 0;

    public bool CanQuickPin => IsQuickPinned || !IsEffectivelyHidden;

    public bool IsGroupMember { get => _isGroupMember; set { if (Set(ref _isGroupMember, value)) Raise(nameof(CardOpacity)); } }

    public double CardOpacity => !IsConnected ? 0.4 : IsEffectivelyHidden && !IsGroupMember ? 0.5 : 1.0;

    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(Math.Round(value), 0, 100);
            if (!Set(ref _volume, value))
            {
                return;
            }

            Raise(nameof(VolumeText));
            if (_suppressWrite || IsVolumeLocked)
            {
                return;
            }

            _lastUserVolume = DateTime.UtcNow;
            if (_ruleState.PinnedMute is null && IsMuted != (value <= 0))
            {
                SetMuted(value <= 0, write: true);
            }

            _ = Owner.Writer.SetVolumeAsync(Endpoint, (float)(value / 100));
        }
    }

    public bool IsMuted { get => _isMuted; private set { if (Set(ref _isMuted, value)) RaiseVisuals(); } }

    public string VolumeText => IsMuted ? "Muted" : $"{(int)Volume}%";

    public double VolumeAreaOpacity => IsMuted ? 0.2 : 1.0;

    public bool IsVolumeLocked => IsConnected && (_ruleState.PinnedVolume is not null || _ruleState.PinnedMute?.Value == true);

    public bool IsVolumeEditable => IsConnected && !IsVolumeLocked;

    public bool IsMuteEditable => IsConnected && _ruleState.PinnedMute is null;

    public bool ShowVolumeLockIcon => _ruleState.PinnedVolume is not null && ShowVolumeControls;

    public string? PinnedMuteRule => _ruleState.PinnedMute?.SourceName;

    public bool? PinnedMuteTarget => _ruleState.PinnedMute?.Value;

    public string VolumeLockedTooltip =>
        _ruleState.PinnedVolume is { } v ? $"Volume locked by rule '{v.SourceName}'"
        : _ruleState.PinnedMute is { Value: true } m ? $"Volume disabled while mute rule '{m.SourceName}' silences this device"
        : "Volume locked by rule";

    public string MuteTooltip =>
        _ruleState.PinnedMute is { } m ? $"{(IsMuted ? "Mute" : "Unmute")} locked by rule '{m.SourceName}'"
        : (IsMuted ? "Unmute " : "Mute ") + (IsRender ? "output" : "input");

    public string MeterTooltip => Owner.Settings.PeakMeterChannelMode == PeakMeterChannelMode.Combined ? "Peak meter: all channels combined"
        : ChannelCount <= 1 ? "Peak meter: Mono"
        : ChannelCount == 2 ? "Peak meter (top to bottom): Left, Right"
        : "Peak meter (top to bottom): Left, Centre / LFE, Right";

    public IReadOnlyList<RuleSummary> Rules => _ruleState.Rules;

    public RuleSummary? FirstRule => Rules.Count > 0 ? Rules[0] : null;

    public IReadOnlyList<RuleSummary> AdditionalRules => [.. Rules.Skip(1)];

    public bool HasRules => Rules.Count > 0;

    public bool HasNoRules => Rules.Count == 0;

    public bool HasMultipleRules => Rules.Count > 1;

    public string MoreRulesLabel => Rules.Count == 2 ? "Show 1 more rule" : $"Show {Rules.Count - 1} more rules";

    public bool IsRulesExpanded { get => _isRulesExpanded; set => Set(ref _isRulesExpanded, value); }

    public bool HasRowApps => Apps.Any(a => a.IsInRow);

    public NowPlayingViewModel? PrimaryNowPlaying => NowPlaying.Count > 0 ? NowPlaying[0] : null;

    public IImage? BackgroundArt => PrimaryNowPlaying?.BlurredArtwork;

    public bool HasNowPlaying => NowPlaying.Count > 0;

    public string Glyph => DeviceVisuals.ResolveStoredGlyph(Config.Glyph) ?? AutoGlyph;

    public string AutoGlyph => _waveLinkGlyph ?? _nameGlyph ?? (IsRender ? (IsMuted ? DeviceVisuals.SpeakerMute : DeviceVisuals.Speaker) : (IsMuted ? DeviceVisuals.MicOff : DeviceVisuals.Mic));

    public Color? AutoAccent => _waveLinkAccent ?? _autoAccent;

    public Color? EffectiveAccent => DeviceVisuals.AccentNone.Equals(Config.AccentColour, StringComparison.OrdinalIgnoreCase) ? null
        : DeviceVisuals.ParseHex(Config.AccentColour) ?? AutoAccent;

    public Bitmap? WaveLinkIcon => Config.AccentColour is null ? _waveLinkIcon : null;

    public bool ShowWaveLinkIcon => WaveLinkIcon is not null;

    public bool ShowGlyph => !ShowWaveLinkIcon;

    public bool ShowAccentTile => EffectiveAccent is not null && !IsMuted && !ShowWaveLinkIcon;

    public IBrush? TileBrush => ShowAccentTile ? new SolidColorBrush(EffectiveAccent!.Value) : null;

    public IBrush GlyphBrush => ShowAccentTile ? new SolidColorBrush(DeviceVisuals.ContrastFor(EffectiveAccent!.Value)) : IsMuted ? CriticalBrush : AccentTextBrush;

    public bool IsBluetoothBusy { get => _isBluetoothBusy; private set { if (Set(ref _isBluetoothBusy, value)) Raise(nameof(BluetoothTooltip)); } }

    public string BluetoothGlyph => char.ConvertFromUtf32(IsConnected ? 57899 : 57713);

    public string BluetoothTooltip => IsBluetoothBusy ? "Connecting..." : IsConnected ? "Disconnect this Bluetooth device" : "Connect this Bluetooth device";

    public CardOptions OptionsFor(bool overlay) => overlay ? OverlayOptions : PageOptions;

    public void Refresh(AudioEndpoint endpoint, bool connected, bool bluetooth, float? volume, bool? muted, DeviceConfig config)
    {
        var nameChanged = endpoint.FriendlyName != Endpoint.FriendlyName;
        Endpoint = endpoint;
        IsConnected = connected;
        IsBluetooth = bluetooth;
        Config = config;
        if (nameChanged)
        {
            _nameGlyph = DeviceVisuals.GlyphForName(Endpoint.Name);
        }

        if (volume is { } v)
        {
            SyncVolume(v, force: true);
        }

        if (muted is { } m)
        {
            IsMuted = m;
        }

        RefreshOptions();
        RaiseAll();
    }

    public void ApplyConfig(DeviceConfig config)
    {
        Config = config;
        RefreshOptions();
        RaiseAll();
    }

    public void RefreshOptions()
    {
        PageOptions.Apply(Owner.Settings, overlay: false, Config);
        OverlayOptions.Apply(Owner.Settings, overlay: true, Config);
        Raise(nameof(MeterTooltip));
        foreach (var chip in Apps)
        {
            chip.RaiseLockBadge();
        }
    }

    public void ApplyRules(DeviceRuleState state)
    {
        var wasHidden = IsEffectivelyHidden;
        _ruleState = state;
        Raise(nameof(Rules));
        Raise(nameof(FirstRule));
        Raise(nameof(AdditionalRules));
        Raise(nameof(HasRules));
        Raise(nameof(HasNoRules));
        Raise(nameof(HasMultipleRules));
        Raise(nameof(MoreRulesLabel));
        Raise(nameof(IsVolumeLocked));
        Raise(nameof(IsVolumeEditable));
        Raise(nameof(IsMuteEditable));
        Raise(nameof(ShowVolumeLockIcon));
        Raise(nameof(VolumeLockedTooltip));
        Raise(nameof(MuteTooltip));
        if (wasHidden != IsEffectivelyHidden)
        {
            Raise(nameof(IsEffectivelyHidden));
            Raise(nameof(CardOpacity));
            Raise(nameof(CanQuickPin));
        }
    }

    public void SyncVolume(float level, bool force = false)
    {
        if (!force && (_ruleState.PinnedVolume is not null || DateTime.UtcNow - _lastUserVolume < UserVolumeGrace))
        {
            return;
        }

        _suppressWrite = true;
        Volume = level * 100;
        _suppressWrite = false;
    }

    public void SyncMuted(bool muted) => IsMuted = muted;

    public void SetMuted(bool muted, bool write)
    {
        IsMuted = muted;
        if (write)
        {
            _ = SetMutedChecked(muted);
        }
    }

    public void SetVolumeAndMute(double volume, bool muted)
    {
        _suppressWrite = true;
        Volume = volume;
        _suppressWrite = false;
        IsMuted = muted;
        _ = Owner.Writer.SetVolumeAsync(Endpoint, (float)(volume / 100));
        _ = Owner.Writer.SetMutedAsync(Endpoint, muted);
    }

    public void UpdatePeaks(EndpointChannelPeaks peaks, TimeSpan tick)
    {
        ChannelCount = peaks.ChannelCount;
        LeftLevel = peaks.Left;
        RightLevel = peaks.Right;
        CentreLevel = peaks.CentreLfe;
        var now = DateTime.UtcNow;
        var peak = MathF.Max(peaks.Left, MathF.Max(peaks.Right, peaks.CentreLfe));
        if (peak >= Hold)
        {
            Hold = peak;
            _holdExpiry[0] = now.AddSeconds(PeakHoldSeconds);
        }
        else if (now > _holdExpiry[0])
        {
            Hold = MathF.Max(peak, Hold - (PeakHoldDecayPerSecond * (float)tick.TotalSeconds));
        }
    }

    public void SetWaveLinkVisual(Color? accent, Bitmap? icon, string? glyph)
    {
        if (accent == _waveLinkAccent && icon == _waveLinkIcon && glyph == _waveLinkGlyph)
        {
            return;
        }

        _waveLinkAccent = accent;
        _waveLinkIcon = icon;
        _waveLinkGlyph = glyph;
        RaiseVisuals();
    }

    public async Task ToggleBluetoothAsync()
    {
        if (IsBluetoothBusy)
        {
            return;
        }

        IsBluetoothBusy = true;
        Owner.RequestBluetooth(this);
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        IsBluetoothBusy = false;
    }

    public void PlayPing()
    {
        if (IsRender && IsConnected)
        {
            var id = Endpoint.Id;
            _ = Task.Run(() => Owner.Endpoints.PlayTestPing(id));
        }
    }

    public void RaiseNowPlaying()
    {
        Raise(nameof(PrimaryNowPlaying));
        Raise(nameof(BackgroundArt));
        Raise(nameof(HasNowPlaying));
        Raise(nameof(HasRowApps));
    }

    public void RaiseApps() => Raise(nameof(HasRowApps));

    private async Task SetMutedChecked(bool muted)
    {
        if (!await Owner.Writer.SetMutedAsync(Endpoint, muted).ConfigureAwait(true))
        {
            var id = Endpoint.Id;
            if (await Task.Run(() => Owner.Endpoints.GetMuted(id)).ConfigureAwait(true) is { } actual)
            {
                IsMuted = actual;
            }
        }
    }

    private void RaiseVisuals()
    {
        Raise(nameof(Glyph));
        Raise(nameof(AutoGlyph));
        Raise(nameof(EffectiveAccent));
        Raise(nameof(WaveLinkIcon));
        Raise(nameof(ShowWaveLinkIcon));
        Raise(nameof(ShowGlyph));
        Raise(nameof(ShowAccentTile));
        Raise(nameof(TileBrush));
        Raise(nameof(GlyphBrush));
        Raise(nameof(VolumeText));
        Raise(nameof(VolumeAreaOpacity));
        Raise(nameof(MuteTooltip));
    }
}
