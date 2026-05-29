using CommunityToolkit.Mvvm.ComponentModel;

using Earmark.App.Services;
using Earmark.Core.Audio;
using Earmark.Core.Models;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Earmark.App.ViewModels;

/// <summary>
/// One application chip on a <see cref="DeviceCard"/>'s Apps row. Carries the session
/// identity needed for drag/drop reroute, the rule-lock state that gates dragging, the
/// pid-keyed peak level, and the lazily-loaded icon.
/// </summary>
public partial class AppChip : ObservableObject
{
    private readonly ISessionIconService _iconService;

    // Audio passthrough / mixer / virtual-cable apps surface as ordinary sessions but they're
    // just relaying audio from somewhere else. They clutter the apps row without telling the
    // user anything they can act on - dragging Wave Link's own session to a different output
    // would be nonsensical. Substring match (case-insensitive) against process name + exe
    // path - one keyword catches the whole vendor's process zoo (WaveLink, WaveLinkApp,
    // WaveLinkHelper all match "wavelink") without having to enumerate every variant.
    private static readonly string[] AudioPassthroughKeywords = new[]
    {
        // Elgato Wave Link
        "wavelink", "wave link",
        // Voicemeeter family
        "voicemeeter", "vbvmaux", "vmvirtualaudio",
        // VB-Audio Cable
        "vbaudio", "vbcable", "vb-cable",
        // Nvidia Broadcast
        "nvidia broadcast", "nvbroadcast",
        // Generic audio routers
        "audiorelay", "audio router", "soundvolumeview",
        "audiorepeater", "audiorepeatermke",
        "virtualaudiocable", "virtual audio cable",
        // Steam VR audio mirror
        "vrserver",
    };

    public static bool ShouldShowAsAppChip(AudioSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        // Hide Earmark itself - the test-tone "ping" briefly opens a session against the
        // target endpoint, and a chip for our own process on the very card it's pinging is
        // confusing (and untouchable - the rule-lock check would block dragging anyway).
        if (session.ProcessId == (uint)Environment.ProcessId) return false;
        // System Sounds appears as a session on every active render endpoint, which clutters
        // the row with the same icon over and over. It also isn't reroutable per-app (it's
        // the OS, not an app), so a chip for it tells the user nothing actionable.
        if (session.IsSystemSounds) return false;
        var name = session.ProcessName ?? string.Empty;
        var path = session.ExecutablePath ?? string.Empty;
        foreach (var keyword in AudioPassthroughKeywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                path.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    public AppChip(AudioSession session, string placementEndpointId, ISessionIconService iconService, RoutingRule? lockingRule)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        PlacementEndpointId = placementEndpointId ?? throw new ArgumentNullException(nameof(placementEndpointId));
        _iconService = iconService ?? throw new ArgumentNullException(nameof(iconService));
        LockingRule = lockingRule;
        LastAudibleAt = DateTime.UtcNow;

        // First call kicks the async load; subsequent calls (driven by the peak tick) pick
        // up the cached ImageSource once the load completes.
        Icon = string.IsNullOrEmpty(session.ExecutablePath)
            ? null
            : _iconService.TryGetIcon(session.ProcessId, session.ExecutablePath);
    }

    /// <summary>Wall-clock timestamp of the last tick whose peak crossed the audibility
    /// threshold. Drives the "actively or recently produced audio" filter at the HomeViewModel
    /// level - chips silent for longer than the grace window are removed from the card.</summary>
    public DateTime LastAudibleAt { get; private set; }

    /// <summary>Peak amplitude below which we treat the session as silent. -46 dBFS is comfortably
    /// below speech-noise but well above the digital-zero floor most clean exits land at.</summary>
    public const float AudibleAmplitudeThreshold = 0.005f;

    public AudioSession Session { get; }
    public RoutingRule? LockingRule { get; }
    public bool IsRuleLocked => LockingRule is not null;
    public bool CanDrag => !IsRuleLocked && !Session.IsSystemSounds;
    public uint ProcessId => Session.ProcessId;

    /// <summary>
    /// Endpoint the chip is physically rendered on. NAudio's <see cref="AudioSession.CurrentEndpointId"/>
    /// reports where the session was *created*, not where audio currently flows - a
    /// per-app-default routing change leaves it stale. We track placement explicitly so the
    /// chip can migrate cards as audio shifts (driven by live peak signal in <c>HomeViewModel</c>).
    /// </summary>
    public string PlacementEndpointId { get; }
    public string SourceEndpointId => PlacementEndpointId;

    public string LockTooltip
    {
        get
        {
            if (LockingRule is { } rule)
            {
                var ruleName = string.IsNullOrWhiteSpace(rule.Name) ? "Unnamed rule" : rule.Name;
                return $"Locked by rule \"{ruleName}\"";
            }
            return string.Empty;
        }
    }

    /// <summary>Tooltip body: display name + path; when rule-locked, the rule name as well.</summary>
    public string Tooltip
    {
        get
        {
            var name = Session.IsSystemSounds ? "System Sounds" : Session.DisplayName;
            var path = string.IsNullOrEmpty(Session.ExecutablePath) ? Session.ProcessName : Session.ExecutablePath;
            var head = string.IsNullOrEmpty(path) || string.Equals(name, path, StringComparison.OrdinalIgnoreCase)
                ? name
                : $"{name}\n{path}";
            if (LockingRule is { } rule)
            {
                var ruleName = string.IsNullOrWhiteSpace(rule.Name) ? "Unnamed rule" : rule.Name;
                return $"{head}\n\nPinned by rule \"{ruleName}\" - drag is disabled.";
            }
            return head;
        }
    }

    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    public bool HasIcon => Icon is not null;
    public bool HasNoIcon => Icon is null;

    partial void OnIconChanged(ImageSource? value)
    {
        OnPropertyChanged(nameof(HasIcon));
        OnPropertyChanged(nameof(HasNoIcon));
    }

    [ObservableProperty]
    public partial float PeakLevel { get; set; }

    public double ChipOpacity => IsRuleLocked ? 0.72 : 1.0;

    /// <summary>Tick entry point. Updates peak and, if the icon hasn't arrived yet, re-queries
    /// the cache (the icon service loads asynchronously on first request).</summary>
    public void Tick(float peak)
    {
        PeakLevel = MathF.Min(peak, 1f);
        if (peak >= AudibleAmplitudeThreshold)
        {
            LastAudibleAt = DateTime.UtcNow;
        }
        if (Icon is null && !string.IsNullOrEmpty(Session.ExecutablePath))
        {
            Icon = _iconService.TryGetIcon(Session.ProcessId, Session.ExecutablePath);
        }
    }

    // Log-scaled bar position so the chip meter reads in dB the same way the device peak
    // meter does. Linear amplitude pinned to a usable [-60, 0] dBFS window then mapped
    // [0..1] for the bar width.
    private const float PeakMinDb = -60f;

    private static double DbBar(float amplitude)
    {
        if (amplitude <= 0f) return 0;
        var db = 20.0 * Math.Log10(amplitude);
        if (db <= PeakMinDb) return 0;
        return Math.Clamp((db - PeakMinDb) / -PeakMinDb, 0.0, 1.0);
    }

    public GridLength PeakBarLeftStars =>
        new GridLength(Math.Max(DbBar(PeakLevel), 0.0001), GridUnitType.Star);

    public GridLength PeakBarRightStars =>
        new GridLength(Math.Max(1.0 - DbBar(PeakLevel), 0.0001), GridUnitType.Star);

    partial void OnPeakLevelChanged(float value)
    {
        OnPropertyChanged(nameof(PeakBarLeftStars));
        OnPropertyChanged(nameof(PeakBarRightStars));
    }
}
