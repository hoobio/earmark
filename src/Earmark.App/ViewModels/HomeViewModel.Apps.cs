using System.Collections.ObjectModel;
using System.Globalization;

using Earmark.App.Services;
using Earmark.App.Settings;
using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Routing;

using Microsoft.Extensions.Logging;

namespace Earmark.App.ViewModels;

/// <summary>App-indicator chip machinery for the Devices page: the in-place per-card chip reconcile,
/// chip ordering, the hidden-apps lookup, and the hide / close / terminate actions. Split out of the
/// main <see cref="HomeViewModel"/> partial for readability (all plain methods).</summary>
public partial class HomeViewModel
{
    /// <summary>
    /// Reconciles the card's <see cref="DeviceCard.Apps"/> collection with the current
    /// session snapshot. Mutates in place (Add / Remove on the ObservableCollection) so the
    /// XAML implicit animations fire per chip; replacing the collection wholesale tears down
    /// and re-creates every chip on every refresh, which both kills the fade and forces an
    /// icon re-load.
    /// </summary>
    private void SyncCardApps(
        DeviceCard card,
        IReadOnlyList<AudioSession> sessions,
        Dictionary<uint, AppRouteMatch?> routeByPid,
        HashSet<string> existsIdentities,
        Dictionary<string, AudioSession> liveSessionByIdentity,
        bool alwaysShowPinned)
    {
        if (card.Endpoint.Flow != EndpointFlow.Render)
        {
            if (card.Apps.Count > 0)
            {
                card.Apps.Clear();
                NotifyCardApps(card);
            }
            return;
        }

        // Drop any chip whose app the user has hidden - globally, or on this card's device (either set
        // may have grown since the chip was placed). The additions loop below skips both too, so
        // nothing re-adds them.
        if (_hiddenAppKeys.Count > 0 || _hiddenAppOnDeviceKeys.Count > 0)
        {
            var removedHidden = false;
            for (var i = card.Apps.Count - 1; i >= 0; i--)
            {
                var ik = card.Apps[i].Session.IdentityKey;
                if (IsAppHidden(ik) || IsAppHiddenOnDevice(ik, card.Endpoint.Id))
                {
                    card.Apps.RemoveAt(i);
                    removedHidden = true;
                }
            }
            if (removedHidden) NotifyCardApps(card);
        }

        // Classify + age out existing chips. An app whose identity is on no live source (no audio
        // session, no running process) has exited -> mark the chip closed (it lingers, dimmed +
        // badged). An app that's back -> revive a stale closed chip, adopting its live session.
        // Either way, remove the chip once it's lingered past the configured window. The "audible
        // right now" check protects a still-playing app from a momentary snapshot gap.
        var now = DateTime.UtcNow;
        var linger = LingerWindow;
        for (var i = card.Apps.Count - 1; i >= 0; i--)
        {
            var chip = card.Apps[i];
            var key = chip.Session.IdentityKey;
            var peak = _sessionMeters.GetPeak(chip.ProcessId, card.Endpoint.Id) ?? 0f;
            var audibleHere = peak >= AppChip.AudibleAmplitudeThreshold;
            var exists = audibleHere || existsIdentities.Contains(key);

            if (!exists)
            {
                chip.MarkClosed();
            }
            else if (chip.IsClosed && liveSessionByIdentity.TryGetValue(key, out var live))
            {
                // App reopened with a live audio session - re-adopt it (and its rule match).
                routeByPid.TryGetValue(live.ProcessId, out var liveMatch);
                chip.Revive(live, liveMatch?.Rule, _processControl.CanControl(live.ProcessId), _processControl.CanClose(live.ProcessId), _processControl.IsElevated(live.ProcessId));
            }

            // Never prune a chip that's audible right now: the run state can be stale here (the
            // 20Hz tick that advances it is paused while the Home page is off-screen). The visible
            // path's Phase 3 prune ages idle/closed chips out with a fresh clock.
            if (!audibleHere && ShouldPrune(chip, now, linger, alwaysShowPinned))
            {
                _logger.LogInformation("Chip removed: pid={Pid} key='{Key}' card={Card} closed={Closed}",
                    chip.ProcessId, key, card.Endpoint.DisplayName, chip.IsClosed);
                card.Apps.RemoveAt(i);
                NotifyCardApps(card);
            }
        }

        // Collapse any pre-existing duplicate chips for the same app (an app spawns several
        // processes), keeping the loudest so the survivor's meter reflects real output. One chip
        // per app remains, keyed by executable path.
        var existingByApp = new Dictionary<string, AppChip>(StringComparer.Ordinal);
        for (var i = card.Apps.Count - 1; i >= 0; i--)
        {
            var chip = card.Apps[i];
            var key = chip.Session.IdentityKey;
            if (existingByApp.TryGetValue(key, out var kept))
            {
                var keptPeak = _sessionMeters.GetPeak(kept.ProcessId, card.Endpoint.Id) ?? 0f;
                var thisPeak = _sessionMeters.GetPeak(chip.ProcessId, card.Endpoint.Id) ?? 0f;
                if (thisPeak > keptPeak)
                {
                    var keptIndex = card.Apps.IndexOf(kept);
                    if (keptIndex >= 0) card.Apps.RemoveAt(keptIndex);
                    existingByApp[key] = chip;
                }
                else
                {
                    card.Apps.RemoveAt(i);
                }
            }
            else
            {
                existingByApp[key] = chip;
            }
        }

        // A session belongs on this render card when EITHER it's audible here now OR an enabled
        // ApplicationOutput rule pins it to this endpoint (and the process is running, i.e. it's
        // in the snapshot). Audibility uses the live peak cache; rule-pinning uses the pre-resolved
        // route map (no matcher calls here). Silent-but-pinned apps still get a chip so a running
        // app shows under its device before it makes a sound. Processes of one app collapse to a
        // single chip, keyed by executable path.
        var rulePinnedApps = new HashSet<string>(StringComparer.Ordinal);
        var additions = new Dictionary<string, (AudioSession Session, bool Audible, RoutingRule? Rule, bool PinnedHere)>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (!ShouldShow(session)) continue;

            var key = session.IdentityKey;
            if (IsAppHidden(key) || IsAppHiddenOnDevice(key, card.Endpoint.Id)) continue;
            routeByPid.TryGetValue(session.ProcessId, out var match);
            var pinnedHere = match is not null &&
                string.Equals(match.Endpoint.Id, card.Endpoint.Id, StringComparison.OrdinalIgnoreCase);
            if (pinnedHere) rulePinnedApps.Add(key);

            if (existingByApp.ContainsKey(key)) continue;

            var livePeak = _sessionMeters.GetPeak(session.ProcessId, card.Endpoint.Id) ?? 0f;
            var audible = livePeak >= AppChip.AudibleAmplitudeThreshold;
            // A silent app earns a chip only when a rule pins it here AND the always-show setting is
            // on. Off, a pinned-but-silent app is treated like any other silent app (no chip).
            if (!audible && !(pinnedHere && alwaysShowPinned)) continue;

            // One addition per app, preferring an audible representative (so its meter shows real
            // audio) and carrying any rule match for the lock badge.
            if (additions.TryGetValue(key, out var cur))
            {
                var rep = (audible && !cur.Audible) ? session : cur.Session;
                additions[key] = (rep, cur.Audible || audible, cur.Rule ?? match?.Rule, cur.PinnedHere || pinnedHere);
            }
            else
            {
                additions[key] = (session, audible, match?.Rule, pinnedHere);
            }
        }

        // Past the classify/prune sweep above, this loop does ADDITIONS only (plus the rule-pin
        // flag refresh below). Removal is never tied to "peak says silent right now" - a chip lingers
        // until it's been idle or closed past the configured window (the sweep here and the Phase 3
        // tick prune), so a brief gap (track change, pause, seek) can't yank it.
        foreach (var add in additions.Values
            .OrderBy(a => a.Session.IsSystemSounds ? "System Sounds" : a.Session.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var chip = new AppChip(add.Session, card.Endpoint.Id, _iconService, _meterOptions, add.Rule, startsActive: add.Audible, ownerCard: card, onHide: HideApp, onHideOnDevice: HideAppOnDevice, onClose: CloseApp, onTerminate: TerminateApp, canControlProcess: _processControl.CanControl(add.Session.ProcessId), canCloseProcess: _processControl.CanClose(add.Session.ProcessId), isElevated: _processControl.IsElevated(add.Session.ProcessId))
            {
                RulePinnedHere = add.PinnedHere,
            };
            InsertChipSorted(card.Apps, chip);
            // Full identity at creation: the icon is loaded from ExecutablePath while the label
            // uses DisplayName. If a stale PID-keyed process-info cache makes those disagree
            // (e.g. display='Audacity' but path points at msedge.exe), the chip shows a mismatched
            // icon - this line makes that obvious in the log.
            _logger.LogInformation(
                "Chip placed: pid={Pid} name='{Name}' display='{Display}' path='{Path}' key='{Key}' card='{Card}' pinnedHere={Pinned} audible={Audible}",
                add.Session.ProcessId, add.Session.ProcessName, add.Session.DisplayName,
                add.Session.ExecutablePath, add.Session.IdentityKey, card.Endpoint.DisplayName,
                add.PinnedHere, add.Audible);
        }

        // Refresh the cached rule-pin flag on every surviving chip: a rule may have started or
        // stopped pinning this app since the chip was created. When it flips false the now-silent
        // chip becomes eligible for the Phase 3 grace prune again.
        foreach (var chip in card.Apps)
        {
            chip.RulePinnedHere = rulePinnedApps.Contains(chip.Session.IdentityKey);
        }

        // Pin / close state just changed for some chips - re-sort so the audio-activity order holds.
        SortCardApps(card);

        NotifyCardApps(card);
    }

    /// <summary>Peak for the chip's app on an endpoint: the max live peak across every process of
    /// that app (one chip stands in for them all). Falls back to the chip's own pid if the app
    /// isn't in the grouped snapshot.</summary>
    private float MaxPeakForApp(AppChip chip, string endpointId, Dictionary<string, List<uint>> pidsByAppKey)
    {
        if (pidsByAppKey.TryGetValue(chip.Session.IdentityKey, out var pids))
        {
            var best = 0f;
            foreach (var pid in pids)
            {
                var p = _sessionMeters.GetPeak(pid, endpointId) ?? 0f;
                if (p > best) best = p;
            }
            return best;
        }
        return _sessionMeters.GetPeak(chip.ProcessId, endpointId) ?? 0f;
    }

    /// <summary>
    /// Front-to-back tier for a chip (higher sorts earlier): 3 playing now, 2 played then stopped,
    /// 1 never produced audio, 0 closed. Ordering is driven purely by audio activity - rule-pinning
    /// no longer weights the order (it still keeps a silent chip from being pruned).
    /// </summary>
    private static int ChipTier(AppChip c)
    {
        if (c.IsClosed) return 0;
        if (c.PlayingSince is not null) return 3;
        if (c.LastStoppedAt is not null) return 2;
        return 1;
    }

    /// <summary>Orders chips by <see cref="ChipTier"/> (descending), then within a tier: playing
    /// chips by start time ascending (first to start sits in front), stopped chips by stop time
    /// descending (most recently stopped in front), and the rest alphabetically for a stable order.
    /// Returns &lt;0 when <paramref name="a"/> sorts before <paramref name="b"/>.</summary>
    private static int CompareChips(AppChip a, AppChip b)
    {
        var byTier = ChipTier(b).CompareTo(ChipTier(a));
        if (byTier != 0) return byTier;

        if (a.PlayingSince is { } aStart && b.PlayingSince is { } bStart)
        {
            var byStart = aStart.CompareTo(bStart);
            if (byStart != 0) return byStart;
        }
        else if (a.LastStoppedAt is { } aStop && b.LastStoppedAt is { } bStop)
        {
            var byStop = bStop.CompareTo(aStop);
            if (byStop != 0) return byStop;
        }

        return string.Compare(NameOf(a), NameOf(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string NameOf(AppChip c) =>
        c.Session.IsSystemSounds ? "System Sounds" : c.Session.DisplayName;

    private static void InsertChipSorted(ObservableCollection<AppChip> chips, AppChip chip)
    {
        for (var i = 0; i < chips.Count; i++)
        {
            if (CompareChips(chip, chips[i]) < 0)
            {
                chips.Insert(i, chip);
                return;
            }
        }
        chips.Add(chip);
    }

    /// <summary>
    /// Assigns each chip its <see cref="AppChip.WrapOrder"/> rank in <see cref="CompareChips"/> order.
    /// The apps-row <see cref="Controls.WrapPanel"/> arranges chips by that rank, NOT by collection
    /// order, so a re-sort re-positions the SAME containers and each moved chip's implicit Offset
    /// animation glides it to its new slot. We deliberately do NOT <see cref="ObservableCollection{T}.Move"/>
    /// the collection: a Move makes the ItemsControl recreate the moved chip's container, which lands
    /// fresh at its destination with no slide (only the chips that stayed would animate). The
    /// collection keeps its arrival order; chip instance identity is preserved so icons / bindings survive.
    /// </summary>
    private void SortCardApps(DeviceCard card)
    {
        var chips = card.Apps;
        if (chips.Count == 0) return;

        var desired = new List<AppChip>(chips);
        desired.Sort(CompareChips);

        var changed = false;
        for (var i = 0; i < desired.Count; i++)
        {
            if (desired[i].WrapOrder != i)
            {
                desired[i].WrapOrder = i;
                changed = true;
            }
        }

        if (changed)
        {
            _logger.LogInformation("Apps re-sorted on '{Card}': {Order}",
                card.Endpoint.DisplayName,
                string.Join(" > ", desired.Select(c => $"{c.DisplayLabel}[t{ChipTier(c)}]")));
        }
    }

    /// <summary>
    /// Per-app default endpoint override. Called by the Home page drag/drop handler when a
    /// chip lands on a render card whose endpoint id differs from the chip's source. Calls
    /// straight into <see cref="IAudioPolicyService"/>; the OS routes new audio streams to
    /// the target on the next session activation. The session snapshot rebuild that the
    /// session manager fires picks the chip up on the new card.
    /// </summary>
    public void MoveSessionToEndpoint(AppChip chip, AudioEndpoint targetEndpoint)
    {
        ArgumentNullException.ThrowIfNull(chip);
        ArgumentNullException.ThrowIfNull(targetEndpoint);
        if (targetEndpoint.Flow != EndpointFlow.Render) return;
        if (chip.IsRuleLocked) return;
        if (string.Equals(chip.SourceEndpointId, targetEndpoint.Id, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var pid = chip.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _policy.SetDefaultEndpointForApp(pid, targetEndpoint.Id, RoleScope.All, EndpointFlow.Render);
        }
        catch
        {
            // Errors surface via the logger inside the policy service.
        }
    }

    /// <summary>Rebuilds <see cref="_hiddenAppKeys"/> and <see cref="_hiddenAppOnDeviceKeys"/> from
    /// settings and refreshes the count baselines used to detect changes in
    /// <see cref="OnSettingsChanged"/>.</summary>
    private void RefreshHiddenApps()
    {
        _hiddenAppKeys.Clear();
        foreach (var app in _settings.Current.HiddenApps)
        {
            if (!string.IsNullOrEmpty(app.Key)) _hiddenAppKeys.Add(app.Key);
        }
        _lastHiddenAppsCount = _settings.Current.HiddenApps.Count;

        _hiddenAppOnDeviceKeys.Clear();
        foreach (var app in _settings.Current.HiddenAppsOnDevice)
        {
            if (!string.IsNullOrEmpty(app.Key) && !string.IsNullOrEmpty(app.EndpointId))
                _hiddenAppOnDeviceKeys.Add(DeviceHideKey(app.Key, app.EndpointId));
        }
        _lastHiddenAppsOnDeviceCount = _settings.Current.HiddenAppsOnDevice.Count;
    }

    /// <summary>Composite lookup key for a per-device hide: the app's identity key and the card's
    /// endpoint id joined by NUL (both compared case-insensitively).</summary>
    private static string DeviceHideKey(string identityKey, string endpointId) =>
        identityKey + "\0" + endpointId;

    private bool IsAppHidden(string identityKey) => _hiddenAppKeys.Contains(identityKey);

    private bool IsAppHiddenOnDevice(string identityKey, string endpointId) =>
        _hiddenAppOnDeviceKeys.Count > 0 && _hiddenAppOnDeviceKeys.Contains(DeviceHideKey(identityKey, endpointId));

    /// <summary>
    /// Permanently hides an app from every device card's chip row (the chip's "Hide this app"
    /// context menu). Records the app's identity key + friendly name in settings, drops every chip
    /// for it now for instant feedback, and persists. The app keeps routing / playing; only its
    /// chip is suppressed. Reversible from Settings &gt; App indicators.
    /// </summary>
    public void HideApp(AppChip chip)
    {
        ArgumentNullException.ThrowIfNull(chip);
        var key = chip.Session.IdentityKey;
        if (string.IsNullOrEmpty(key)) return;

        PushLayoutUndo();   // Ctrl+Z un-hides the app
        if (!_settings.Current.HiddenApps.Any(h => string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.Current.HiddenApps.Add(new HiddenApp { Key = key, Name = chip.DisplayLabel });
        }
        // Update the live set + count baseline now so the 20Hz tick can't re-add the chip and our
        // own save's SettingsChanged doesn't trigger a redundant reconcile.
        _hiddenAppKeys.Add(key);
        _lastHiddenAppsCount = _settings.Current.HiddenApps.Count;

        // An app can be audible on more than one card; drop every chip that matches.
        foreach (var card in _allCards)
        {
            var removed = false;
            for (var i = card.Apps.Count - 1; i >= 0; i--)
            {
                if (string.Equals(card.Apps[i].Session.IdentityKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    card.Apps.RemoveAt(i);
                    removed = true;
                }
            }
            if (removed) NotifyCardApps(card);
        }

        _logger.LogInformation("App hidden from chip rows: key='{Key}'", key);
        QueueSettingsSave();
    }

    /// <summary>
    /// Hides an app's chip on the owning card's device only (the chip's "Hide this app &gt; On this
    /// device" context menu). The app still shows on every other card. Records the app's identity key
    /// + the card's endpoint id (plus friendly names for the manage list), drops the matching chip on
    /// that card now, and persists. Reversible from Settings &gt; App indicators.
    /// </summary>
    public void HideAppOnDevice(AppChip chip)
    {
        ArgumentNullException.ThrowIfNull(chip);
        var key = chip.Session.IdentityKey;
        var endpointId = chip.OwnerCard?.Endpoint.Id ?? chip.PlacementEndpointId;
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(endpointId)) return;

        PushLayoutUndo();   // Ctrl+Z un-hides the app
        if (!_settings.Current.HiddenAppsOnDevice.Any(h =>
                string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(h.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.Current.HiddenAppsOnDevice.Add(new HiddenAppOnDevice
            {
                Key = key,
                EndpointId = endpointId,
                Name = chip.DisplayLabel,
                DeviceName = chip.OwnerCard?.Endpoint.FriendlyName,
            });
        }
        // Update the live set + count baseline now so the 20Hz tick can't re-add the chip and our
        // own save's SettingsChanged doesn't trigger a redundant reconcile.
        _hiddenAppOnDeviceKeys.Add(DeviceHideKey(key, endpointId));
        _lastHiddenAppsOnDeviceCount = _settings.Current.HiddenAppsOnDevice.Count;

        // Drop only the chip on the matching card; the app stays on every other card.
        foreach (var card in _allCards)
        {
            if (!string.Equals(card.Endpoint.Id, endpointId, StringComparison.OrdinalIgnoreCase)) continue;
            var removed = false;
            for (var i = card.Apps.Count - 1; i >= 0; i--)
            {
                if (string.Equals(card.Apps[i].Session.IdentityKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    card.Apps.RemoveAt(i);
                    removed = true;
                }
            }
            if (removed) NotifyCardApps(card);
        }

        _logger.LogInformation("App hidden from chip row on one device: key='{Key}' endpoint='{Endpoint}'", key, endpointId);
        QueueSettingsSave();
    }

    /// <summary>Gracefully closes an app from its chip's context menu (WM_CLOSE, the SIGTERM
    /// equivalent - the app can save / prompt). On success the chip is flagged user-closed so it
    /// prunes on the short window instead of the full linger; failures the disabled state couldn't
    /// pre-empt (a controllable app with no window, or a race) surface as a toast.</summary>
    public void CloseApp(AppChip chip)
    {
        ArgumentNullException.ThrowIfNull(chip);
        var pids = AppProcessIds(chip);
        var result = _processControl.Close(pids);
        _logger.LogInformation("Close requested: pids=[{Pids}] name='{Name}' result={Result}",
            string.Join(",", pids), chip.DisplayLabel, result);

        switch (result)
        {
            case ProcessActionResult.Success:
                MarkUserClosed(chip);
                break;
            case ProcessActionResult.NoWindow:
                _inAppNotifications.Show($"{chip.DisplayLabel} has no window to close. Shift + right-click it to Terminate.");
                break;
            case ProcessActionResult.AccessDenied:
                // We could terminate it (or the item would be disabled), but Windows refused the
                // graceful WM_CLOSE - it runs at a higher privilege. Point at the action that works.
                _inAppNotifications.Show($"Windows blocked closing {chip.DisplayLabel}. Shift + right-click it to Terminate.");
                break;
            case ProcessActionResult.NotFound:
                break;   // already gone - the chip's close-detection catches up on its own
            default:
                _inAppNotifications.Show($"Couldn't close {chip.DisplayLabel}. See the log for details.");
                break;
        }
    }

    /// <summary>Force-terminates an app from its chip's shift-revealed context menu (TerminateProcess,
    /// the SIGKILL equivalent - no save, no prompt). Same user-closed / toast handling as
    /// <see cref="CloseApp"/>.</summary>
    public void TerminateApp(AppChip chip)
    {
        ArgumentNullException.ThrowIfNull(chip);
        var pids = AppProcessIds(chip);
        var result = _processControl.Kill(pids);
        _logger.LogInformation("Terminate requested: pids=[{Pids}] name='{Name}' result={Result}",
            string.Join(",", pids), chip.DisplayLabel, result);

        switch (result)
        {
            case ProcessActionResult.Success:
                MarkUserClosed(chip);
                break;
            case ProcessActionResult.AccessDenied:
                _inAppNotifications.Show($"Couldn't terminate {chip.DisplayLabel} - access denied.");
                break;
            case ProcessActionResult.NotFound:
                break;
            default:
                _inAppNotifications.Show($"Couldn't terminate {chip.DisplayLabel}. See the log for details.");
                break;
        }
    }

    /// <summary>Every running pid that shares the chip's app identity (its lowercase executable path),
    /// so close / terminate act on the whole app, not just the one process holding the audio session.
    /// A browser fans out into GPU / renderer / audio children all running the same exe; the chip's
    /// pid is usually a child, so closing it alone does nothing visible. Unions the running-process
    /// snapshot and the live session list (either may know a pid the other hasn't caught yet), always
    /// including the chip's own pid.</summary>
    private HashSet<uint> AppProcessIds(AppChip chip)
    {
        var key = chip.Session.IdentityKey;
        var pids = new HashSet<uint> { chip.ProcessId };
        foreach (var process in _processes.GetRunningProcesses())
        {
            if (string.Equals(process.IdentityKey, key, StringComparison.OrdinalIgnoreCase)) pids.Add(process.ProcessId);
        }
        foreach (var session in _sessions.GetSessions())
        {
            if (string.Equals(session.IdentityKey, key, StringComparison.OrdinalIgnoreCase)) pids.Add(session.ProcessId);
        }
        return pids;
    }

    /// <summary>Flags every chip of the just-closed app (it can sit on more than one card) so the
    /// prune drops them on the short user-closed window once the process exits.</summary>
    private void MarkUserClosed(AppChip chip)
    {
        var key = chip.Session.IdentityKey;
        foreach (var card in _allCards)
        {
            foreach (var c in card.Apps)
            {
                if (string.Equals(c.Session.IdentityKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    c.UserClosed = true;
                }
            }
        }
    }
}
