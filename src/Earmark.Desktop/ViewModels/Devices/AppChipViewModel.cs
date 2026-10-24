using Avalonia.Media.Imaging;

using Earmark.Core.Models;

namespace Earmark.Desktop.ViewModels.Devices;

public sealed class AppChipViewModel : Observable
{
    public const float AudibleThreshold = 0.00025f;
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(4);

    private static readonly string[] ForwarderKeywords =
    [
        "wavelink", "wave link", "voicemeeter", "vbvmaux", "vmvirtualaudio", "vban", "vbaudio", "vbcable", "vb-cable",
        "nvidia broadcast", "nvbroadcast", "rtxvoice", "rtx voice", "steelseries sonar", "steelseriessonar",
        "synchronousaudiorouter", "synchronous audio router", "dante virtual", "dantevirtual", "jackrouter", "jack audio",
        "voicemod", "clownfish", "audiorelay", "audio router", "soundvolumeview", "audiorepeater", "virtualaudiocable",
        "virtual audio cable", "vrserver",
    ];

    private DateTime? _silentSince;
    private Bitmap? _icon;
    private bool _rulePinnedHere;
    private bool _isInNowPlaying;
    private RoutingRule? _lockingRule;

    public AppChipViewModel(DeviceCardViewModel owner, AudioSession session, RoutingRule? lockingRule, bool startsActive, (bool CanTerminate, bool CanClose, bool Elevated) process)
    {
        Owner = owner;
        Session = session;
        _lockingRule = lockingRule;
        (CanTerminate, CanClose, IsElevated) = process;
        if (startsActive)
        {
            PlayingSince = DateTime.UtcNow;
        }

        LoadIcon();
    }

    public DeviceCardViewModel Owner { get; }

    public AudioSession Session { get; private set; }

    public uint ProcessId => Session.ProcessId;

    public string Key => Session.IdentityKey;

    public float Peak { get; set; }

    public DateTime? PlayingSince { get; private set; }

    public DateTime? LastStoppedAt { get; private set; }

    public DateTime? ClosedAt { get; private set; }

    public bool UserClosed { get; set; }

    public bool IsClosed => ClosedAt is not null;

    public bool IsActive => PlayingSince is not null;

    public bool CanTerminate { get; private set; }

    public bool CanClose { get; private set; }

    public bool IsElevated { get; private set; }

    public Bitmap? Icon { get => _icon; private set => Set(ref _icon, value); }

    public bool HasIcon => Icon is not null;

    public string FallbackGlyph => Session.IsSystemSounds ? DeviceVisuals.Speaker : DeviceVisuals.AppGeneric;

    public string Label => Session.IsSystemSounds ? "System Sounds" : Session.DisplayName;

    public string Tooltip
    {
        get
        {
            var path = string.IsNullOrEmpty(Session.ExecutablePath) ? Session.ProcessName : Session.ExecutablePath;
            return string.IsNullOrEmpty(path) || string.Equals(Label, path, StringComparison.OrdinalIgnoreCase) ? Label : $"{Label}\n{path}";
        }
    }

    public bool IsRuleLocked => _lockingRule is not null;

    public string LockTooltip => _lockingRule is { } r ? $"Locked by rule \"{(string.IsNullOrWhiteSpace(r.Name) ? "Unnamed rule" : r.Name)}\"" : string.Empty;

    public bool ShowLockBadge => IsRuleLocked && Owner.Owner.Settings.AlwaysShowPinnedApps;

    public bool CanDrag => !IsRuleLocked && !IsClosed && !Session.IsSystemSounds;

    public bool ShowProcessActions => !Session.IsSystemSounds && !IsClosed;

    public double Opacity => IsClosed ? 0.3 : IsActive ? 1.0 : 0.45;

    public bool RulePinnedHere { get => _rulePinnedHere; set => _rulePinnedHere = value; }

    public bool IsInNowPlaying
    {
        get => _isInNowPlaying;
        set
        {
            if (Set(ref _isInNowPlaying, value))
            {
                Raise(nameof(IsInRow));
            }
        }
    }

    public bool IsInRow => !IsInNowPlaying;

    public int Tier => IsClosed ? 0 : IsActive ? 3 : LastStoppedAt is not null ? 2 : 1;

    public static bool ShouldShow(AudioSession session, bool filterForwarders)
    {
        if (session.ProcessId == (uint)Environment.ProcessId)
        {
            return false;
        }

        if (!filterForwarders)
        {
            return true;
        }

        var name = session.ProcessName ?? string.Empty;
        var path = session.ExecutablePath ?? string.Empty;
        return !ForwarderKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase) || path.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    public static int Compare(AppChipViewModel a, AppChipViewModel b)
    {
        var byTier = b.Tier.CompareTo(a.Tier);
        if (byTier != 0)
        {
            return byTier;
        }

        if (a.PlayingSince is { } aStart && b.PlayingSince is { } bStart && aStart != bStart)
        {
            return aStart.CompareTo(bStart);
        }

        if (a.LastStoppedAt is { } aStop && b.LastStoppedAt is { } bStop && aStop != bStop)
        {
            return bStop.CompareTo(aStop);
        }

        return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
    }

    public bool Tick(float peak)
    {
        Peak = MathF.Min(peak, 1f);
        var wasTier = Tier;
        var now = DateTime.UtcNow;
        if (peak >= AudibleThreshold)
        {
            _silentSince = null;
            ClosedAt = null;
            PlayingSince ??= now;
        }
        else if (PlayingSince is not null)
        {
            _silentSince ??= now;
            if (now - _silentSince.Value >= ActiveWindow)
            {
                LastStoppedAt = _silentSince;
                PlayingSince = null;
                _silentSince = null;
            }
        }

        if (wasTier == Tier)
        {
            return false;
        }

        RaiseState();
        return true;
    }

    public void MarkClosed()
    {
        if (ClosedAt is not null)
        {
            return;
        }

        ClosedAt = DateTime.UtcNow;
        if (PlayingSince is not null)
        {
            LastStoppedAt = ClosedAt;
            PlayingSince = null;
        }

        _silentSince = null;
        RaiseState();
    }

    public void Revive(AudioSession session, RoutingRule? lockingRule, (bool CanTerminate, bool CanClose, bool Elevated) process)
    {
        Session = session;
        _lockingRule = lockingRule;
        ClosedAt = null;
        UserClosed = false;
        (CanTerminate, CanClose, IsElevated) = process;
        if (Icon is null)
        {
            LoadIcon();
        }

        RaiseAll();
    }

    public void SetLockingRule(RoutingRule? rule)
    {
        if (!ReferenceEquals(rule, _lockingRule))
        {
            _lockingRule = rule;
            Raise(nameof(IsRuleLocked));
            Raise(nameof(LockTooltip));
            Raise(nameof(ShowLockBadge));
            Raise(nameof(CanDrag));
        }
    }

    public void RaiseLockBadge() => Raise(nameof(ShowLockBadge));

    private void RaiseState()
    {
        Raise(nameof(Opacity));
        Raise(nameof(IsClosed));
        Raise(nameof(CanDrag));
        Raise(nameof(ShowProcessActions));
    }

    private async void LoadIcon()
    {
        var path = string.IsNullOrEmpty(Session.ExecutablePath) ? null : Session.ExecutablePath;
        if (path is null)
        {
            return;
        }

        Icon = await Owner.Owner.Images.AppIconAsync(path).ConfigureAwait(true);
        Raise(nameof(HasIcon));
    }
}
