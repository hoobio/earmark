using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Earmark.App.ViewModels;

/// <summary>One now-playing strip paired with the view's display options, so the strip's data template
/// (which can't reach the host control's options) renders at the right window's density. <see cref="Strip"/>
/// is the shared strip VM (all live state); <see cref="Options"/> is this view's options.</summary>
public sealed record NowPlayingStripView(NowPlayingStrip Strip, PeakMeterOptions Options);

/// <summary>
/// Per-view presentation of a <see cref="DeviceCard"/>: pairs the (shared) card with the display
/// <see cref="PeakMeterOptions"/> for the window rendering it, and recomputes the visibility flags that
/// combine an option with card data (rules / now-playing / badges / dividers / volume-row). The main
/// window builds one with the card's global options; Quick Controls builds one with its own options, so
/// the same card renders to each window's settings without duplicating the card. Pure styling/data reads,
/// re-raised when either side changes; live data, commands, and per-device state stay on the card.
/// </summary>
public sealed partial class CardPresentation : ObservableObject, IDisposable
{
    private readonly DeviceCard _card;
    private readonly PeakMeterOptions _options;

    public CardPresentation(DeviceCard card, PeakMeterOptions options)
    {
        _card = card ?? throw new ArgumentNullException(nameof(card));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _card.PropertyChanged += OnCardChanged;
        _options.PropertyChanged += OnOptionsChanged;
        foreach (var strip in _card.NowPlayingStrips) NowPlayingStrips.Add(new NowPlayingStripView(strip, _options));
        ((INotifyCollectionChanged)_card.NowPlayingStrips).CollectionChanged += OnStripsChanged;
    }

    /// <summary>The shared card (live data, commands, per-device state).</summary>
    public DeviceCard Card => _card;

    /// <summary>This view's display options (geometry + feature flags).</summary>
    public PeakMeterOptions Options => _options;

    /// <summary>The card's now-playing strips paired with this view's options, mirrored in place from
    /// <see cref="DeviceCard.NowPlayingStrips"/>. The now-playing ItemsControl binds this so each strip's
    /// data template gets the right window's density (the template can't see the host's Options DP).</summary>
    public ObservableCollection<NowPlayingStripView> NowPlayingStrips { get; } = new();

    private void OnStripsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                for (var i = 0; i < e.NewItems.Count; i++)
                    NowPlayingStrips.Insert(e.NewStartingIndex + i, new NowPlayingStripView((NowPlayingStrip)e.NewItems[i]!, _options));
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                for (var i = 0; i < e.OldItems.Count; i++) NowPlayingStrips.RemoveAt(e.OldStartingIndex);
                break;
            default:
                NowPlayingStrips.Clear();
                foreach (var strip in _card.NowPlayingStrips) NowPlayingStrips.Add(new NowPlayingStripView(strip, _options));
                break;
        }
    }

    private bool Compact => _options.CompactCards;

    // ---- Rules ----
    public bool ShowRulesSection => _options.ShowRules && _card.HasRules;
    public bool ShowNoRulesMessage => _options.ShowRules && _card.HasNoRules;

    // ---- Now playing ----
    public bool ShowNowPlaying => _card.NowPlayingStrips.Count > 0 && _options.ShowNowPlaying;
    public bool ShowCardBackground => _options.ShowNowPlaying && _options.NowPlayingCardBackground && _card.PrimaryNowPlaying is not null;

    // ---- Apps row ----
    public bool ShowAppsSection => _card.HasRowApps && _options.ShowAppIndicators;

    // ---- Header badges ----
    public bool ShowNormalBadges => _options.ShowDeviceBadges && !Compact;
    public bool ShowCompactBadges => _options.ShowDeviceBadges && Compact;

    // ---- Volume row ----
    public bool ShowVolumeRow => _options.ShowMeter || _card.ShowVolumeControls;
    public bool ShowPlainSlider => !_options.ShowMeter && _card.ShowVolumeControls;

    // ---- Section dividers (mirror DeviceCard's original combine logic, sourced from this view's options) ----
    public bool ShowNowPlayingDivider => _options.ShowCardDividers && ShowNowPlaying && ShowCardBackground;

    public bool ShowAppsDivider => _options.ShowCardDividers && ShowAppsSection
        && (ShowCardBackground || !ShowNowPlaying);

    public bool ShowRulesDivider => _options.ShowCardDividers && (ShowRulesSection || ShowNoRulesMessage)
        && !(ShowNowPlaying && !ShowCardBackground && !ShowAppsSection);

    private void OnOptionsChanged(object? sender, PropertyChangedEventArgs e) => RaiseAll();

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DeviceCard.HasRules):
            case nameof(DeviceCard.HasNoRules):
            case nameof(DeviceCard.HasRowApps):
            case nameof(DeviceCard.ShowVolumeControls):
            case nameof(DeviceCard.PrimaryNowPlaying):
            case nameof(DeviceCard.ShowNowPlaying):          // card raises this when strips reconcile
            case nameof(DeviceCard.ShowAppsSection):
            case nameof(DeviceCard.ShowCardBackground):
                RaiseAll();
                break;
        }
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(ShowRulesSection));
        OnPropertyChanged(nameof(ShowNoRulesMessage));
        OnPropertyChanged(nameof(ShowNowPlaying));
        OnPropertyChanged(nameof(ShowCardBackground));
        OnPropertyChanged(nameof(ShowAppsSection));
        OnPropertyChanged(nameof(ShowNormalBadges));
        OnPropertyChanged(nameof(ShowCompactBadges));
        OnPropertyChanged(nameof(ShowVolumeRow));
        OnPropertyChanged(nameof(ShowPlainSlider));
        OnPropertyChanged(nameof(ShowNowPlayingDivider));
        OnPropertyChanged(nameof(ShowAppsDivider));
        OnPropertyChanged(nameof(ShowRulesDivider));
    }

    public void Dispose()
    {
        _card.PropertyChanged -= OnCardChanged;
        _options.PropertyChanged -= OnOptionsChanged;
        ((INotifyCollectionChanged)_card.NowPlayingStrips).CollectionChanged -= OnStripsChanged;
    }
}
