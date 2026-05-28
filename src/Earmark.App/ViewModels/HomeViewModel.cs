using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Earmark.App.Services;
using Earmark.App.Settings;
using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Routing;
using Earmark.Core.Services;
using Earmark.Core.WaveLink;

using Microsoft.UI.Xaml;

namespace Earmark.App.ViewModels;

/// <summary>
/// Orchestrates the Home page: discovers active audio endpoints, builds <see cref="DeviceCard"/>
/// instances from them (rule summary + initial volume/mute state), filters the visible set
/// against user-hidden / no-rules state, and polls peak + mute on a single timer.
/// </summary>
public partial class HomeViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PeakTickInterval = TimeSpan.FromMilliseconds(50);
    private const int MutePollEveryNthTick = 5;

    private readonly IRulesService _rules;
    private readonly IAudioEndpointService _endpoints;
    private readonly IAudioSessionService _sessions;
    private readonly IRuleMatcher _matcher;
    private readonly IRuleEvaluator _evaluator;
    private readonly ISettingsService _settings;
    private readonly IWaveLinkService _waveLink;
    private readonly IDispatcherQueueProvider _dispatcher;
    private readonly INotificationService _notifications;
    private readonly Dictionary<string, DateTime> _lastReconcileToast = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ToastRateLimit = TimeSpan.FromSeconds(15);
    private readonly Lock _gate = new();
    private readonly List<DeviceCard> _allCards = new();
    private readonly DeviceUndoStack _undoStack = new();
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _settingsSaveCts;
    private DispatcherTimer? _peakTimer;
    private int _muteTickCounter;

    public HomeViewModel(
        IRulesService rules,
        IAudioEndpointService endpoints,
        IAudioSessionService sessions,
        IRuleMatcher matcher,
        IRuleEvaluator evaluator,
        ISettingsService settings,
        IWaveLinkService waveLink,
        INotificationService notifications,
        IDispatcherQueueProvider dispatcher)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _waveLink = waveLink ?? throw new ArgumentNullException(nameof(waveLink));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        // Show-hidden is session-only: defaults to off on every launch.

        _rules.RulesChanged += OnAnythingChanged;
        _endpoints.EndpointsChanged += OnAnythingChanged;
        _endpoints.DefaultsChanged += OnAnythingChanged;
        _endpoints.ExternalMuteChanged += OnExternalMuteChanged;
        _sessions.SessionsChanged += OnAnythingChanged;
        _waveLink.SnapshotChanged += OnAnythingChanged;
        _waveLink.StateChanged += OnAnythingChanged;

        QueueRefresh();
        StartPeakPolling();
    }

    public ObservableCollection<DeviceCard> Devices { get; } = new();

    public bool HasItems => Devices.Count > 0;
    public bool IsEmpty => Devices.Count == 0;

    [ObservableProperty]
    public partial bool ShowHiddenDevices { get; set; }

    partial void OnShowHiddenDevicesChanged(bool value)
    {
        foreach (var card in _allCards)
        {
            card.RefreshListed(value);
        }
        SyncVisibleDevices();
    }

    /// <summary>
    /// Debounces settings writes so rapid toggling of <see cref="ShowHiddenDevices"/> doesn't
    /// queue a backlog of file writes (each with its 5-attempt retry loop) that could lag the
    /// displayed state. The latest in-memory value wins.
    /// </summary>
    private void QueueSettingsSave()
    {
        _settingsSaveCts?.Cancel();
        _settingsSaveCts = new CancellationTokenSource();
        var token = _settingsSaveCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try { await _settings.SaveAsync(token).ConfigureAwait(false); }
            catch { /* SettingsService logs internally */ }
        }, token);
    }

    // -------- Periodic peak + mute polling --------

    private void StartPeakPolling()
    {
        _peakTimer = new DispatcherTimer { Interval = PeakTickInterval };
        _peakTimer.Tick += OnPeakTick;
        _peakTimer.Start();
    }

    private void OnPeakTick(object? sender, object e)
    {
        // Event-driven mute notifications (AudioEndpointService.ExternalMuteChanged) handle
        // the fast path; this poll is the fallback safety net for any miss. Runs every ~250ms.
        var pollMute = ++_muteTickCounter % MutePollEveryNthTick == 0;
        foreach (var card in Devices)
        {
            var level = _endpoints.GetPeakLevel(card.Endpoint.Id) ?? 0f;
            card.UpdatePeak(level, PeakTickInterval);

            if (pollMute)
            {
                var muted = _endpoints.GetMuted(card.Endpoint.Id);
                if (muted.HasValue)
                {
                    ApplyExternalMute(card, muted.Value);
                }
            }
        }
    }

    private void OnExternalMuteChanged(object? sender, EndpointMuteChangedEventArgs e)
    {
        // Callback arrives on a COM thread; marshal to the UI thread before touching VMs.
        var deviceId = e.DeviceId;
        var muted = e.Muted;
        _dispatcher.Enqueue(() =>
        {
            var card = _allCards.FirstOrDefault(c =>
                string.Equals(c.Endpoint.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            if (card is null) return;
            ApplyExternalMute(card, muted);
        });
    }

    /// <summary>
    /// Updates the card's cached mute state to match what the OS reports, then snaps it back
    /// to the rule-pinned target if that target disagrees (rule wins over external changes).
    /// Surfaces a Windows toast naming the rule so the user understands why their external
    /// change didn't stick. Rate-limited per device to avoid spam.
    /// </summary>
    private void ApplyExternalMute(DeviceCard card, bool actualMuted)
    {
        card.SyncMutedFromDevice(actualMuted);

        if (card.RuleMutedTarget is not bool target || actualMuted == target) return;

        _endpoints.SetMuted(card.Endpoint.Id, target);
        card.SyncMutedFromDevice(target);
        NotifyReconciled(card, target);
    }

    private void NotifyReconciled(DeviceCard card, bool restoredToMuted)
    {
        var now = DateTime.UtcNow;
        if (_lastReconcileToast.TryGetValue(card.Endpoint.Id, out var last) &&
            now - last < ToastRateLimit)
        {
            return;
        }
        _lastReconcileToast[card.Endpoint.Id] = now;

        var ruleName = card.RuleMutedSource ?? "an active rule";
        var verb = restoredToMuted ? "muted" : "unmuted";
        _notifications.Show(
            $"Earmark kept '{card.Endpoint.FriendlyName}' {verb}",
            $"Rule \"{ruleName}\" pins this device, so the external change was reverted.");
    }

    // -------- Rebuild + visibility filtering --------

    private void OnAnythingChanged(object? sender, EventArgs e) => QueueRefresh();

    private void QueueRefresh()
    {
        CancellationToken token;
        lock (_gate)
        {
            _refreshCts?.Cancel();
            _refreshCts = new CancellationTokenSource();
            token = _refreshCts.Token;
        }

        _ = RebuildAsync(token);
    }

    private async Task RebuildAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(DebounceWindow, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var hiddenIds = new HashSet<string>(_settings.Current.HiddenDeviceIds, StringComparer.OrdinalIgnoreCase);
        var pinnedIds = new HashSet<string>(_settings.Current.PinnedDeviceIds, StringComparer.OrdinalIgnoreCase);
        var showHidden = ShowHiddenDevices;

        var built = await Task.Run(() => BuildCards(hiddenIds, pinnedIds, showHidden), ct).ConfigureAwait(false);

        if (ct.IsCancellationRequested) return;

        _dispatcher.Enqueue(() =>
        {
            if (ct.IsCancellationRequested) return;
            _allCards.Clear();
            _allCards.AddRange(built);
            SyncVisibleDevices();
        });
    }

    private List<DeviceCard> BuildCards(HashSet<string> hiddenIds, HashSet<string> pinnedIds, bool showHidden)
    {
        var renderEndpoints = _endpoints.GetEndpoints(EndpointFlow.Render);
        var captureEndpoints = _endpoints.GetEndpoints(EndpointFlow.Capture);
        var sessions = _sessions.GetSessions();
        var rules = _rules.Rules;
        var waveLinkOutputDeviceNames = BuildWaveLinkDeviceNameSet(_waveLink.LastSnapshot);

        // Sort tiers (top -> bottom):
        //   1. System default render  (output before input within defaults)
        //   2. System default capture
        //   3. System default-communications render (only relevant when distinct from #1)
        //   4. System default-communications capture
        //   5. Wave Link mix targets (physical "listening" endpoints)
        //   6. Wave Link virtual channels (Elgato Virtual Audio pairs)
        //   7. Everything else
        // Within each tier: render before capture, then alphabetical.
        var ordered = renderEndpoints.Concat(captureEndpoints)
            .Where(e => e.State == EndpointState.Active)
            .OrderByDescending(e => e.IsDefault && e.Flow == EndpointFlow.Render)
            .ThenByDescending(e => e.IsDefault)
            .ThenByDescending(e => e.IsDefaultCommunications && e.Flow == EndpointFlow.Render)
            .ThenByDescending(e => e.IsDefaultCommunications)
            .ThenByDescending(e => IsWaveLinkMixTarget(e, waveLinkOutputDeviceNames))
            .ThenByDescending(e => IsWaveLinkVirtualChannel(e))
            .ThenBy(e => e.Flow)
            .ThenBy(e => e.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cards = new List<DeviceCard>(ordered.Count);
        foreach (var endpoint in ordered)
        {
            var summary = DeviceRulesSummary.For(endpoint, rules, renderEndpoints, captureEndpoints, sessions, _matcher, _evaluator);
            var volume = _endpoints.GetVolume(endpoint.Id) ?? 0f;
            var muted = _endpoints.GetMuted(endpoint.Id) ?? false;
            var hiddenByUser = hiddenIds.Contains(endpoint.Id);
            var pinnedByUser = pinnedIds.Contains(endpoint.Id);

            cards.Add(new DeviceCard(
                _endpoints,
                endpoint,
                volume,
                muted,
                summary.VolumeLocked,
                summary.MuteLocked,
                summary.RuleMutedTarget,
                summary.RuleMutedSource,
                summary.RuleVolumeSource,
                summary.Rules,
                hiddenByUser,
                pinnedByUser,
                showHidden,
                OnCardVisibilityToggled));
        }
        return cards;
    }

    /// <summary>
    /// Pulls the list of physical playback endpoint names Wave Link is currently routing
    /// mixed audio to ("Headphones", "Speakers", etc.). These rank above the Elgato virtual
    /// channels in the device grid.
    /// </summary>
    private static HashSet<string> BuildWaveLinkDeviceNameSet(WaveLinkSnapshot? snapshot)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (snapshot is null) return names;
        foreach (var output in snapshot.OutputDevices)
        {
            if (!string.IsNullOrEmpty(output.DeviceName))
            {
                names.Add(output.DeviceName);
            }
        }
        return names;
    }

    /// <summary>A physical playback endpoint Wave Link sends mixed audio to.</summary>
    private static bool IsWaveLinkMixTarget(AudioEndpoint endpoint, HashSet<string> waveLinkOutputDeviceNames)
        => waveLinkOutputDeviceNames.Contains(endpoint.FriendlyName);

    /// <summary>An Elgato virtual channel pair (Game, Comms, Media, etc.).</summary>
    private static bool IsWaveLinkVirtualChannel(AudioEndpoint endpoint)
        => !string.IsNullOrEmpty(endpoint.DeviceDescription)
           && endpoint.DeviceDescription.Contains("Elgato Virtual Audio", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Diffs <see cref="_allCards"/> against the visible <see cref="Devices"/> collection.
    /// Preserves instance identity for cards that stay visible so the UI doesn't churn
    /// (avoids tearing down peak meters and slider bindings on every event).
    /// </summary>
    private void SyncVisibleDevices()
    {
        var listed = _allCards.Where(c => c.IsListed).ToList();

        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (!listed.Contains(Devices[i]))
            {
                Devices.RemoveAt(i);
            }
        }

        for (var i = 0; i < listed.Count; i++)
        {
            var card = listed[i];
            var currentIndex = Devices.IndexOf(card);
            if (currentIndex < 0)
            {
                Devices.Insert(i, card);
            }
            else if (currentIndex != i)
            {
                Devices.Move(currentIndex, i);
            }
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OnCardVisibilityToggled(DeviceCard card, DeviceCard.VisibilityState prev)
    {
        _undoStack.PushVisibility(card.Endpoint.Id, prev.IsHidden, prev.IsPinned);
        PersistAndResync(card);
    }

    /// <summary>Records a volume / mute change as a single undo entry. Called by the page
    /// when a slider drag or mute icon click completes.</summary>
    public void RecordVolumeMuteUndo(DeviceCard card, float prevVolume, bool prevMuted)
    {
        // Skip no-ops.
        if (Math.Abs(card.Volume - prevVolume) < 0.001f && card.IsMuted == prevMuted)
        {
            return;
        }
        _undoStack.PushVolumeMute(card.Endpoint.Id, prevVolume, prevMuted);
    }

    /// <summary>Reverts the most recent reversible action (hide/show, volume drag, mute toggle).
    /// Bound to Ctrl+Z on the page.</summary>
    [RelayCommand]
    public void UndoVisibilityChange()
    {
        if (!_undoStack.TryPop(out var action)) return;

        var card = _allCards.FirstOrDefault(c =>
            string.Equals(c.Endpoint.Id, action.DeviceId, StringComparison.OrdinalIgnoreCase));
        if (card is null) return;

        switch (action)
        {
            case DeviceUndoStack.VisibilityUndo v:
                card.SetUserVisibility(v.PrevHidden, v.PrevPinned);
                PersistAndResync(card);
                break;
            case DeviceUndoStack.VolumeMuteUndo vm:
                card.SetVolumeAndMute(vm.PrevVolume, vm.PrevMuted);
                break;
        }
    }

    private void PersistAndResync(DeviceCard card)
    {
        var hiddenList = _settings.Current.HiddenDeviceIds ??= new();
        var pinnedList = _settings.Current.PinnedDeviceIds ??= new();
        SyncIdInList(hiddenList, card.Endpoint.Id, include: card.IsHiddenByUser);
        SyncIdInList(pinnedList, card.Endpoint.Id, include: card.IsPinnedByUser);
        QueueSettingsSave();
        SyncVisibleDevices();
    }

    private static void SyncIdInList(List<string> list, string id, bool include)
    {
        if (include)
        {
            if (!list.Any(existing => string.Equals(existing, id, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(id);
            }
        }
        else
        {
            list.RemoveAll(existing => string.Equals(existing, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Dispose()
    {
        _rules.RulesChanged -= OnAnythingChanged;
        _endpoints.EndpointsChanged -= OnAnythingChanged;
        _endpoints.DefaultsChanged -= OnAnythingChanged;
        _endpoints.ExternalMuteChanged -= OnExternalMuteChanged;
        _sessions.SessionsChanged -= OnAnythingChanged;
        _waveLink.SnapshotChanged -= OnAnythingChanged;
        _waveLink.StateChanged -= OnAnythingChanged;
        if (_peakTimer is not null)
        {
            _peakTimer.Tick -= OnPeakTick;
            _peakTimer.Stop();
            _peakTimer = null;
        }
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _settingsSaveCts?.Cancel();
        _settingsSaveCts?.Dispose();
    }
}
