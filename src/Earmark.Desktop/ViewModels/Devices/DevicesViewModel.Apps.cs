using Earmark.Audio.Services;
using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Routing;
using Earmark.Core.Settings;

using Microsoft.Extensions.Logging;

namespace Earmark.Desktop.ViewModels.Devices;

public sealed partial class DevicesViewModel
{
    private IReadOnlyList<AudioSession>? _tickSessions;
    private Dictionary<string, List<uint>> _tickPidsByKey = [];

    private TimeSpan Linger => TimeSpan.FromSeconds(Math.Clamp(Settings.AppChipLingerSeconds, 0, 600));

    public void MoveChip(AppChipViewModel chip, DeviceCardViewModel target)
    {
        if (!target.IsRender || !target.IsConnected || !chip.CanDrag || SameId(chip.Owner.Endpoint.Id, target.Endpoint.Id))
        {
            return;
        }

        var pid = chip.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var endpointId = target.Endpoint.Id;
        _logger.LogInformation("Drop: pid={Pid} {Source} -> {Target}", chip.ProcessId, chip.Owner.Endpoint.DisplayName, target.Endpoint.DisplayName);
        _ = Task.Run(() => _policy.SetDefaultEndpointForApp(pid, endpointId, RoleScope.All, EndpointFlow.Render))
            .ContinueWith(t => _logger.LogWarning(t.Exception, "Routing a dropped app failed"), TaskContinuationOptions.OnlyOnFaulted);
    }

    public void HideApp(AppChipViewModel chip)
    {
        PushLayoutUndo();
        if (!Settings.HiddenApps.Any(h => SameId(h.Key, chip.Key)))
        {
            Settings.HiddenApps.Add(new HiddenApp { Key = chip.Key, Name = chip.Label });
        }

        _hiddenApps.Add(chip.Key);
        _appsSignature = AppsSignature();
        foreach (var card in _allCards)
        {
            RemoveChips(card, c => SameId(c.Key, chip.Key));
        }

        _logger.LogInformation("App hidden from chip rows: key='{Key}'", chip.Key);
        QueueSave();
    }

    public void HideAppOnDevice(AppChipViewModel chip)
    {
        var endpointId = chip.Owner.Endpoint.Id;
        PushLayoutUndo();
        if (!Settings.HiddenAppsOnDevice.Any(h => SameId(h.Key, chip.Key) && SameId(h.EndpointId, endpointId)))
        {
            Settings.HiddenAppsOnDevice.Add(new HiddenAppOnDevice { Key = chip.Key, EndpointId = endpointId, Name = chip.Label, DeviceName = chip.Owner.Endpoint.FriendlyName });
        }

        _hiddenAppsOnDevice.Add(HideKey(chip.Key, endpointId));
        _appsSignature = AppsSignature();
        RemoveChips(chip.Owner, c => SameId(c.Key, chip.Key));
        QueueSave();
    }

    public void CloseApp(AppChipViewModel chip, bool terminate)
    {
        var pids = ProcessIdsOf(chip);
        var result = terminate ? _processControl.Kill(pids) : _processControl.Close(pids);
        _logger.LogInformation("{Action} requested: pids=[{Pids}] name='{Name}' result={Result}", terminate ? "Terminate" : "Close", string.Join(",", pids), chip.Label, result);
        switch (result)
        {
            case ProcessActionResult.Success:
                foreach (var c in _allCards.SelectMany(c => c.Apps).Where(c => SameId(c.Key, chip.Key)))
                {
                    c.UserClosed = true;
                }

                break;
            case ProcessActionResult.NotFound:
                break;
            case ProcessActionResult.NoWindow when !terminate:
                ShowToast($"{chip.Label} has no window to close. Shift + right-click it to Terminate.");
                break;
            case ProcessActionResult.AccessDenied:
                ShowToast(terminate ? $"Couldn't terminate {chip.Label}: access denied." : $"Windows blocked closing {chip.Label}. Shift + right-click it to Terminate.");
                break;
            default:
                ShowToast($"Couldn't {(terminate ? "terminate" : "close")} {chip.Label}. See the log for details.");
                break;
        }
    }

    private HashSet<uint> ProcessIdsOf(AppChipViewModel chip)
    {
        var pids = new HashSet<uint> { chip.ProcessId };
        pids.UnionWith(_processes.GetRunningProcesses().Where(p => SameId(p.IdentityKey, chip.Key)).Select(p => p.ProcessId));
        pids.UnionWith(_sessions.GetSessions().Where(s => SameId(s.IdentityKey, chip.Key)).Select(s => s.ProcessId));
        return pids;
    }

    private bool IsHidden(string key, string endpointId) => _hiddenApps.Contains(key) || _hiddenAppsOnDevice.Contains(HideKey(key, endpointId));

    private bool ShouldShow(AudioSession s) => AppChipViewModel.ShouldShow(s, Settings.FilterAudioForwarders);

    private (bool, bool, bool) ProcessFlags(uint pid) => (ProcessControlService.CanControl(pid), _processControl.CanClose(pid), ProcessControlService.IsElevated(pid));

    private void ReconcileApps()
    {
        var snapshot = Snapshot();
        var sessions = _sessions.GetSessions();
        var routes = new Dictionary<uint, AppRouteMatch?>();
        var live = new Dictionary<string, AudioSession>(StringComparer.Ordinal);
        foreach (var s in sessions.Where(ShouldShow))
        {
            if (!live.TryGetValue(s.IdentityKey, out var rep) || (s.State == SessionState.Active && rep.State != SessionState.Active))
            {
                live[s.IdentityKey] = s;
            }

            if (!routes.ContainsKey(s.ProcessId))
            {
                routes[s.ProcessId] = snapshot.RouteFor(s);
            }
        }

        var exists = new HashSet<string>(live.Keys, StringComparer.Ordinal);
        var running = _processes.GetRunningProcesses();
        exists.UnionWith(running.Select(p => p.IdentityKey));
        var effective = sessions.ToList();
        if (Settings.AlwaysShowPinnedApps)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var process in running.Where(p => !string.IsNullOrEmpty(p.ExecutablePath)))
            {
                var synthetic = process.ToSyntheticSession();
                if (live.ContainsKey(synthetic.IdentityKey) || !seen.Add(synthetic.IdentityKey) || !ShouldShow(synthetic) || snapshot.RouteFor(synthetic) is not { } match)
                {
                    continue;
                }

                routes[synthetic.ProcessId] = match;
                effective.Add(synthetic);
            }
        }

        _routeByPid = routes;
        foreach (var card in _allCards)
        {
            SyncCardApps(card, effective, exists, live);
        }

        SyncNowPlaying();
        foreach (var card in _allCards)
        {
            card.ApplyRules(snapshot.For(card.Endpoint));
        }

        SyncBlocks();
    }

    private void SyncCardApps(DeviceCardViewModel card, List<AudioSession> sessions, HashSet<string> exists, Dictionary<string, AudioSession> live)
    {
        if (!card.IsRender || !card.PageOptions.ShowAppIndicators)
        {
            RemoveChips(card, _ => true);
            return;
        }

        var endpointId = card.Endpoint.Id;
        RemoveChips(card, c => IsHidden(c.Key, endpointId));
        var now = DateTime.UtcNow;
        var alwaysPinned = Settings.AlwaysShowPinnedApps;
        foreach (var chip in card.Apps.ToList())
        {
            var audible = (_meters.GetPeak(chip.ProcessId, endpointId) ?? 0) >= AppChipViewModel.AudibleThreshold;
            if (!audible && !exists.Contains(chip.Key))
            {
                chip.MarkClosed();
            }
            else if (chip.IsClosed && live.TryGetValue(chip.Key, out var session))
            {
                chip.Revive(session, _routeByPid.GetValueOrDefault(session.ProcessId)?.Rule, ProcessFlags(session.ProcessId));
            }

            if (!audible && ShouldPrune(chip, now, alwaysPinned))
            {
                card.Apps.Remove(chip);
            }
        }

        foreach (var dup in card.Apps.GroupBy(c => c.Key).Where(g => g.Count() > 1).SelectMany(g => g.OrderByDescending(c => _meters.GetPeak(c.ProcessId, endpointId) ?? 0).Skip(1)).ToList())
        {
            card.Apps.Remove(dup);
        }

        var pinnedKeys = new HashSet<string>(StringComparer.Ordinal);
        var additions = new Dictionary<string, (AudioSession Session, bool Audible, RoutingRule? Rule)>(StringComparer.Ordinal);
        foreach (var s in sessions)
        {
            if (!ShouldShow(s) || IsHidden(s.IdentityKey, endpointId))
            {
                continue;
            }

            var match = _routeByPid.GetValueOrDefault(s.ProcessId);
            var pinnedHere = match is not null && SameId(match.Endpoint.Id, endpointId);
            if (pinnedHere)
            {
                pinnedKeys.Add(s.IdentityKey);
            }

            if (card.Apps.Any(c => c.Key == s.IdentityKey))
            {
                continue;
            }

            var audible = (_meters.GetPeak(s.ProcessId, endpointId) ?? 0) >= AppChipViewModel.AudibleThreshold;
            if (!audible && !(pinnedHere && alwaysPinned))
            {
                continue;
            }

            if (!additions.TryGetValue(s.IdentityKey, out var cur) || (audible && !cur.Audible))
            {
                additions[s.IdentityKey] = (s, audible || cur.Audible, match?.Rule ?? cur.Rule);
            }
        }

        foreach (var add in additions.Values)
        {
            Insert(card, new AppChipViewModel(card, add.Session, add.Rule, add.Audible, ProcessFlags(add.Session.ProcessId)));
            _logger.LogInformation("Chip placed: pid={Pid} name='{Name}' card='{Card}' audible={Audible}", add.Session.ProcessId, add.Session.ProcessName, card.Endpoint.DisplayName, add.Audible);
        }

        foreach (var chip in card.Apps)
        {
            chip.RulePinnedHere = pinnedKeys.Contains(chip.Key);
            chip.SetLockingRule(_routeByPid.GetValueOrDefault(chip.ProcessId)?.Rule);
        }

        Sort(card);
    }

    private bool ShouldPrune(AppChipViewModel chip, DateTime now, bool alwaysPinned)
    {
        if (chip.IsClosed)
        {
            return now - chip.ClosedAt!.Value > (chip.UserClosed ? UserClosedLinger : Linger);
        }

        if (chip.RulePinnedHere && alwaysPinned)
        {
            return false;
        }

        return chip.PlayingSince is null && (chip.LastStoppedAt is { } stopped ? now - stopped > Linger : chip.RulePinnedHere && !alwaysPinned);
    }

    private static void Insert(DeviceCardViewModel card, AppChipViewModel chip)
    {
        var at = card.Apps.TakeWhile(c => AppChipViewModel.Compare(chip, c) >= 0).Count();
        card.Apps.Insert(at, chip);
    }

    private static void Sort(DeviceCardViewModel card)
    {
        var sorted = card.Apps.ToList();
        sorted.Sort(AppChipViewModel.Compare);
        for (var i = 0; i < sorted.Count; i++)
        {
            var at = card.Apps.IndexOf(sorted[i]);
            if (at != i)
            {
                card.Apps.Move(at, i);
            }
        }
    }

    private static void RemoveChips(DeviceCardViewModel card, Func<AppChipViewModel, bool> predicate)
    {
        for (var i = card.Apps.Count - 1; i >= 0; i--)
        {
            if (predicate(card.Apps[i]))
            {
                card.Apps.RemoveAt(i);
            }
        }
    }

    private void OnTick()
    {
        _tickCount++;
        var placement = _tickCount % PlacementEveryTicks == 0;
        var sessions = _sessions.GetSessions();
        if (!ReferenceEquals(sessions, _tickSessions))
        {
            _tickSessions = sessions;
            _tickPidsByKey = sessions.Where(ShouldShow).GroupBy(s => s.IdentityKey).ToDictionary(g => g.Key, g => g.Select(s => s.ProcessId).ToList(), StringComparer.Ordinal);
        }

        var pidsByKey = _tickPidsByKey;
        foreach (var card in _visibleCards)
        {
            card.UpdatePeaks(Endpoints.GetChannelPeaks(card.Endpoint.Id) ?? new EndpointChannelPeaks(0, 0, 0, card.ChannelCount), MeterClock.Interval);
            if (!card.PageOptions.ShowAppIndicators && !card.OverlayOptions.ShowAppIndicators)
            {
                continue;
            }

            var reorder = false;
            foreach (var chip in card.Apps)
            {
                reorder |= chip.Tick(MaxPeak(chip.Key, chip.ProcessId, card.Endpoint.Id, pidsByKey));
            }

            if (placement && card.IsRender)
            {
                Place(card, sessions);
                var now = DateTime.UtcNow;
                RemoveChips(card, c => ShouldPrune(c, now, Settings.AlwaysShowPinnedApps));
            }

            if (reorder)
            {
                Sort(card);
            }

            foreach (var strip in card.NowPlaying)
            {
                strip.Tick();
            }
        }
    }

    private void Place(DeviceCardViewModel card, IReadOnlyList<AudioSession> sessions)
    {
        var endpointId = card.Endpoint.Id;
        foreach (var s in sessions)
        {
            if (!ShouldShow(s) || IsHidden(s.IdentityKey, endpointId) || card.Apps.Any(c => c.Key == s.IdentityKey)
                || (_meters.GetPeak(s.ProcessId, endpointId) ?? 0) < AppChipViewModel.AudibleThreshold)
            {
                continue;
            }

            var match = _routeByPid.GetValueOrDefault(s.ProcessId);
            Insert(card, new AppChipViewModel(card, s, match?.Rule, true, ProcessFlags(s.ProcessId)) { RulePinnedHere = match is not null && SameId(match.Endpoint.Id, endpointId) });
            _logger.LogInformation("Chip placed via peak: pid={Pid} name='{Name}' card='{Card}'", s.ProcessId, s.ProcessName, card.Endpoint.DisplayName);
            foreach (var other in _visibleCards.Where(o => o != card && o.IsRender))
            {
                RemoveChips(other, c => c.Key == s.IdentityKey && !c.RulePinnedHere && (_meters.GetPeak(c.ProcessId, other.Endpoint.Id) ?? 0) < AppChipViewModel.AudibleThreshold);
            }
        }
    }

    private float MaxPeak(string key, uint pid, string endpointId, Dictionary<string, List<uint>> pidsByKey) =>
        pidsByKey.TryGetValue(key, out var pids) ? pids.Max(p => _meters.GetPeak(p, endpointId) ?? 0) : _meters.GetPeak(pid, endpointId) ?? 0;

    private void OnNowPlayingChanged()
    {
        var sessions = NowPlaying.GetSessions();
        var keys = new HashSet<string>(sessions.Select(s => s.SessionKey), StringComparer.Ordinal);
        if (!keys.SetEquals(_nowPlayingKeys))
        {
            _nowPlayingKeys = keys;
            QueueApps();
            return;
        }

        var byKey = sessions.ToDictionary(s => s.SessionKey, StringComparer.Ordinal);
        foreach (var strip in _allCards.SelectMany(c => c.NowPlaying))
        {
            if (byKey.TryGetValue(strip.SessionKey, out var info))
            {
                strip.Update(info);
            }
        }
    }

    private void SyncNowPlaying()
    {
        var sessions = _allCards.Any(c => c.PageOptions.ShowNowPlaying || c.OverlayOptions.ShowNowPlaying) ? NowPlaying.GetSessions() : [];
        _nowPlayingKeys = new HashSet<string>(sessions.Select(s => s.SessionKey), StringComparer.Ordinal);
        foreach (var card in _allCards)
        {
            var matches = new List<(AppChipViewModel Chip, NowPlayingInfo Info)>();
            if (card.IsRender && (card.PageOptions.ShowNowPlaying || card.OverlayOptions.ShowNowPlaying))
            {
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var chip in card.Apps.Where(c => !c.IsClosed))
                {
                    if (Match(chip, sessions) is { } info && used.Add(info.SessionKey))
                    {
                        matches.Add((chip, info));
                    }
                }

                matches = [.. matches.OrderByDescending(m => m.Info.IsPlaying).ThenBy(m => m.Info.Title, StringComparer.OrdinalIgnoreCase)];
            }

            foreach (var chip in card.Apps)
            {
                chip.IsInNowPlaying = matches.Any(m => m.Chip == chip);
            }

            for (var i = card.NowPlaying.Count - 1; i >= 0; i--)
            {
                if (!matches.Any(m => m.Chip == card.NowPlaying[i].Chip))
                {
                    card.NowPlaying.RemoveAt(i);
                }
            }

            for (var i = 0; i < matches.Count; i++)
            {
                var (chip, info) = matches[i];
                var existing = card.NowPlaying.FirstOrDefault(s => s.Chip == chip);
                if (existing is null)
                {
                    card.NowPlaying.Insert(Math.Min(i, card.NowPlaying.Count), new NowPlayingViewModel(this, chip, info));
                }
                else
                {
                    existing.Update(info);
                    var at = card.NowPlaying.IndexOf(existing);
                    if (at != i)
                    {
                        card.NowPlaying.Move(at, Math.Min(i, card.NowPlaying.Count - 1));
                    }
                }
            }

            card.RaiseNowPlaying();
        }
    }

    private static NowPlayingInfo? Match(AppChipViewModel chip, IReadOnlyList<NowPlayingInfo> sessions)
    {
        static string Stem(string? n)
        {
            var s = (n ?? string.Empty).ToLowerInvariant();
            return s.EndsWith(".exe", StringComparison.Ordinal) ? s[..^4] : s;
        }

        var proc = Stem(chip.Session.ProcessName);
        var exe = Stem(Path.GetFileName(chip.Session.ExecutablePath));
        var display = (chip.Session.DisplayName ?? string.Empty).ToLowerInvariant();
        NowPlayingInfo? best = null;
        foreach (var info in sessions)
        {
            var t = info.AppMatchToken;
            var hit = t.Length >= 3 && (t == proc || t == exe || (proc.Length >= 3 && (proc.Contains(t, StringComparison.Ordinal) || t.Contains(proc, StringComparison.Ordinal))) || (display.Length > 0 && display.Contains(t, StringComparison.Ordinal)));
            if (hit && (best is null || (info.IsPlaying && !best.IsPlaying)))
            {
                best = info;
            }
        }

        return best;
    }
}
