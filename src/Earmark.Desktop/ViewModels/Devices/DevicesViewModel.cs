using System.Collections.ObjectModel;

using Avalonia.Media;

using Earmark.Audio.Routing;
using Earmark.Audio.Services;
using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Routing;
using Earmark.Core.Services;
using Earmark.Core.Settings;
using Earmark.Core.WaveLink;
using Earmark.Desktop.Services;
using Earmark.Desktop.Services.Devices;
using Earmark.Desktop.Views;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Earmark.Desktop.ViewModels.Devices;

public sealed partial class DevicesViewModel : Observable, IDisposable
{
    private static readonly TimeSpan RebuildDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AppsDebounce = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan UserClosedLinger = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan KnownDeviceMaxAge = TimeSpan.FromDays(90);
    private static readonly TimeSpan ToastRateLimit = TimeSpan.FromSeconds(15);
    private const int MaxKnownDevices = 64;
    private const int PlacementEveryTicks = 5;
    private const int MaxUndo = 50;

    private readonly SettingsService _settings;
    private readonly IRulesService _rules;
    private readonly IAudioSessionService _sessions;
    private readonly IAudioSessionMeterService _meters;
    private readonly IRunningProcessProvider _processes;
    private readonly IAudioPolicyService _policy;
    private readonly IWaveLinkService _waveLink;
    private readonly IBluetoothAudioControl _bluetooth;
    private readonly ProcessControlService _processControl;
    private readonly IServiceProvider _services;
    private readonly ILogger<DevicesViewModel> _logger;
    private readonly List<DeviceCardViewModel> _allCards = [];
    private readonly List<DeviceCardViewModel> _visibleCards = [];
    private readonly Dictionary<string, DeviceGroupViewModel> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hiddenApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hiddenAppsOnDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastMuteToast = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<Action> _undo = new();
    private Dictionary<uint, AppRouteMatch?> _routeByPid = [];
    private HashSet<string> _nowPlayingKeys = new(StringComparer.Ordinal);
    private CancellationTokenSource? _rebuildCts;
    private CancellationTokenSource? _appsCts;
    private CancellationTokenSource? _saveCts;
    private bool _isInitializing = true;
    private bool _showHiddenDevices;
    private int _tickCount;
    private string _appsSignature = string.Empty;
    private WaveLinkChannelStyle? _visualStyle;

    public DevicesViewModel(
        SettingsService settings,
        IRulesService rules,
        IAudioEndpointService endpoints,
        EndpointWriter writer,
        IAudioSessionService sessions,
        IAudioSessionMeterService meters,
        IRunningProcessProvider processes,
        IAudioPolicyService policy,
        RoutingApplier applier,
        IWaveLinkService waveLink,
        IBluetoothAudioControl bluetooth,
        INowPlayingService nowPlaying,
        DeviceDefaultsService defaults,
        ProcessControlService processControl,
        DeviceImages images,
        IServiceProvider services,
        ILogger<DevicesViewModel> logger)
    {
        _settings = settings;
        _rules = rules;
        Endpoints = endpoints;
        Writer = writer;
        _sessions = sessions;
        _meters = meters;
        _processes = processes;
        _policy = policy;
        _waveLink = waveLink;
        _bluetooth = bluetooth;
        NowPlaying = nowPlaying;
        _processControl = processControl;
        Images = images;
        _services = services;
        _logger = logger;

        rules.RulesChanged += (_, _) => Ui.Post(QueueRebuild);
        endpoints.EndpointsChanged += (_, _) => Ui.Post(QueueRebuild);
        endpoints.DefaultsChanged += (_, _) => Ui.Post(QueueRebuild);
        waveLink.StateChanged += (_, _) => Ui.Post(QueueRebuild);
        defaults.DefaultsApplied += (_, _) => Ui.Post(QueueRebuild);
        endpoints.ExternalMuteChanged += (_, e) => Ui.Post(() => OnExternalMute(e.DeviceId, e.Muted));
        endpoints.ExternalVolumeChanged += (_, e) => Ui.Post(() => FindByEndpoint(e.DeviceId)?.SyncVolume(e.Volume));
        sessions.SessionsChanged += (_, _) => Ui.Post(QueueApps);
        processes.ProcessStarted += (_, _) => Ui.Post(QueueApps);
        processes.ProcessStopped += (_, _) => Ui.Post(QueueApps);
        nowPlaying.Changed += (_, _) => Ui.Post(OnNowPlayingChanged);
        applier.RouteApplied += (_, _) => meters.Refresh();
        settings.SettingsChanged += (_, _) => Ui.Post(OnSettingsChanged);
        MeterClock.Tick += OnTick;

        RefreshHiddenApps();
        _appsSignature = AppsSignature();
        Ui.Post(QueueRebuild);
    }

    public IAudioEndpointService Endpoints { get; }

    public EndpointWriter Writer { get; }

    public INowPlayingService NowPlaying { get; }

    public DeviceImages Images { get; }

    public AppSettings Settings => _settings.Current;

    public ObservableCollection<object> Blocks { get; } = [];

    public bool IsInitializing { get => _isInitializing; private set => Set(ref _isInitializing, value); }

    public bool ShowEmptyState => !IsInitializing && Blocks.Count == 0 && _allCards.Count == 0;

    public bool ShowFilteredEmptyState => !IsInitializing && Blocks.Count == 0 && _allCards.Count > 0;

    public bool ShowHiddenDevices
    {
        get => _showHiddenDevices;
        set
        {
            if (Set(ref _showHiddenDevices, value))
            {
                SyncBlocks();
            }
        }
    }

    public bool ShowDisconnectedDevices { get => Settings.ShowDisconnectedDevices; set => SetSetting(value, s => s.ShowDisconnectedDevices, (s, v) => s.ShowDisconnectedDevices = v, resync: true); }

    public bool ShowHeader { get => Settings.ShowDevicesPageHeader; set => SetSetting(value, s => s.ShowDevicesPageHeader, (s, v) => s.ShowDevicesPageHeader = v); }

    public bool LockLayout { get => Settings.LockDeviceLayout; set => SetSetting(value, s => s.LockDeviceLayout, (s, v) => s.LockDeviceLayout = v); }

    public bool ShowRules { get => Settings.ShowRules; set => SetSetting(value, s => s.ShowRules, (s, v) => s.ShowRules = v, options: true); }

    public bool ShowDividers { get => Settings.ShowCardDividers; set => SetSetting(value, s => s.ShowCardDividers, (s, v) => s.ShowCardDividers = v, options: true); }

    public bool ShowBadges { get => Settings.ShowDeviceBadges; set => SetSetting(value, s => s.ShowDeviceBadges, (s, v) => s.ShowDeviceBadges = v, options: true); }

    public bool CompactCards { get => Settings.CompactCards; set => SetSetting(value, s => s.CompactCards, (s, v) => s.CompactCards = v, options: true); }

    public double ColumnMinWidth => Settings.CompactCards ? 256 : 320;

    public CardHeightMode CardHeight => Settings.CardHeight;

    public void RequestBluetooth(DeviceCardViewModel card)
    {
        var id = card.Endpoint.Id;
        var connected = card.IsConnected;
        _ = Task.Run(() =>
        {
            if (connected)
            {
                _bluetooth.RequestDisconnect(id);
            }
            else
            {
                _bluetooth.RequestConnect(id);
            }
        });
    }

    public void Dispose()
    {
        MeterClock.Tick -= OnTick;
        foreach (var cts in new[] { _rebuildCts, _appsCts, _saveCts })
        {
            cts?.Cancel();
            cts?.Dispose();
        }
    }

    public IReadOnlyList<(string? Title, IReadOnlyList<DeviceCardViewModel> Cards)> QuickControlsProjection()
    {
        var result = new List<(string?, IReadOnlyList<DeviceCardViewModel>)>();
        var loose = new List<DeviceCardViewModel>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Flush()
        {
            if (loose.Count > 0)
            {
                result.Add((null, [.. loose]));
                loose.Clear();
            }
        }

        foreach (var id in ComputeOrderedBlockIds(out var groupByMember))
        {
            if (_groups.TryGetValue(id, out var group) && Settings.DeviceGroups.FirstOrDefault(g => SameId(g.Id, id)) is { } record)
            {
                var pinned = record.MemberIds.Select(FindByKey).OfType<DeviceCardViewModel>().Where(c => c.IsQuickPinned && c.IsConnected && used.Add(c.Key)).ToList();
                if (pinned.Count > 0)
                {
                    Flush();
                    result.Add((group.Title, pinned));
                }
            }
            else if (FindByKey(id) is { IsQuickPinned: true, IsConnected: true } card && !groupByMember.ContainsKey(id) && used.Add(card.Key))
            {
                loose.Add(card);
            }
        }

        Flush();
        return result;
    }

    private void SetSetting(bool value, Func<AppSettings, bool> get, Action<AppSettings, bool> set, bool resync = false, bool options = false, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (get(Settings) == value)
        {
            return;
        }

        set(Settings, value);
        Raise(name);
        if (options)
        {
            foreach (var card in _allCards)
            {
                card.RefreshOptions();
            }

            Raise(nameof(ColumnMinWidth));
        }

        if (resync)
        {
            SyncBlocks();
        }

        QueueSave();
    }

    private void QueueSave()
    {
        _saveCts?.Cancel();
        var cts = _saveCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            await Task.Delay(SaveDebounce, cts.Token).ConfigureAwait(false);
            await _settings.SaveAsync(cts.Token).ConfigureAwait(false);
        }).ContinueWith(t => _logger.LogWarning(t.Exception, "Saving settings from the Devices page failed"), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private void QueueRebuild()
    {
        _rebuildCts?.Cancel();
        var cts = _rebuildCts = new CancellationTokenSource();
        _ = RebuildAsync(cts.Token);
    }

    private void QueueApps()
    {
        _appsCts?.Cancel();
        var cts = _appsCts = new CancellationTokenSource();
        _ = Task.Delay(AppsDebounce, cts.Token).ContinueWith(
            _ => Ui.Post(() =>
            {
                if (!cts.IsCancellationRequested && _allCards.Count > 0)
                {
                    ReconcileApps();
                }
            }),
            cts.Token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private sealed record BuildResult(
        List<(AudioEndpoint Endpoint, string Key, bool Connected, bool Bluetooth, float? Volume, bool? Muted)> Devices,
        IReadOnlyDictionary<string, string> IdToKey,
        List<KnownDevice> Known,
        bool KnownChanged,
        List<string> Pruned);

    private async Task RebuildAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(RebuildDebounce, ct).ConfigureAwait(true);
            var known = Settings.KnownDevices.Select(Clone).ToList();
            var built = await Task.Run(() => Build(known, DateTimeOffset.UtcNow), ct).ConfigureAwait(true);
            if (Settings.SettingsSchemaVersion < AppSettings.DeviceKeySchemaVersion)
            {
                await _settings.SaveBackupAsync("backup.v0", ct).ConfigureAwait(true);
            }

            if (!ct.IsCancellationRequested)
            {
                ApplyBuild(built);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Devices rebuild failed");
        }
    }

    private BuildResult Build(List<KnownDevice> known, DateTimeOffset now)
    {
        var live = Endpoints.GetEndpoints(EndpointFlow.Render).Concat(Endpoints.GetEndpoints(EndpointFlow.Capture)).Where(e => e.State == EndpointState.Active).ToList();
        var idToKey = DeviceIdentity.ComputeKeys(live);
        var mixTargets = new HashSet<string>(_waveLink.LastSnapshot?.OutputDevices.Select(o => o.DeviceName) ?? [], StringComparer.OrdinalIgnoreCase);
        var ordered = live
            .OrderByDescending(e => e.IsDefault && e.Flow == EndpointFlow.Render)
            .ThenByDescending(e => e.IsDefault)
            .ThenByDescending(e => e.IsDefaultCommunications && e.Flow == EndpointFlow.Render)
            .ThenByDescending(e => e.IsDefaultCommunications)
            .ThenByDescending(e => mixTargets.Contains(e.FriendlyName))
            .ThenByDescending(e => e.DeviceDescription.Contains("Elgato Virtual Audio", StringComparison.OrdinalIgnoreCase))
            .ThenBy(e => e.Flow)
            .ThenBy(e => e.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var devices = new List<(AudioEndpoint, string, bool, bool, float?, bool?)>();
        var bt = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in ordered)
        {
            bt[e.Id] = e.Flow == EndpointFlow.Render && _bluetooth.IsBluetooth(e.Id);
            devices.Add((e, idToKey[e.Id], true, bt[e.Id], Endpoints.GetVolume(e.Id) ?? 0f, Endpoints.GetMuted(e.Id) ?? false));
        }

        var changed = false;
        var byKey = known.GroupBy(k => k.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var e in ordered)
        {
            var key = idToKey[e.Id];
            if (!byKey.TryGetValue(key, out var row))
            {
                row = byKey[key] = new KnownDevice { Key = key };
                _logger.LogInformation("Known device added: {Key} '{Name}' {Flow}", key, e.FriendlyName, e.Flow);
                changed = true;
            }

            changed |= row.LastEndpointId != e.Id || row.FriendlyName != e.FriendlyName || row.DeviceDescription != e.DeviceDescription
                || row.Flow != e.Flow || !string.Equals(row.ContainerId, e.ContainerId, StringComparison.OrdinalIgnoreCase) || row.IsBluetooth != bt[e.Id]
                || now - row.LastSeenUtc > TimeSpan.FromDays(1);
            (row.LastEndpointId, row.FriendlyName, row.DeviceDescription, row.Flow, row.ContainerId, row.IsBluetooth, row.LastSeenUtc) =
                (e.Id, e.FriendlyName, e.DeviceDescription, e.Flow, e.ContainerId, bt[e.Id], now);
        }

        var liveKeys = new HashSet<string>(idToKey.Values, StringComparer.OrdinalIgnoreCase);
        var pruned = byKey.Values.Where(r => !liveKeys.Contains(r.Key) && now - r.LastSeenUtc > KnownDeviceMaxAge).Select(r => r.Key).ToList();
        var kept = byKey.Values.Where(r => !pruned.Contains(r.Key)).OrderByDescending(r => liveKeys.Contains(r.Key)).ThenByDescending(r => r.LastSeenUtc).ToList();
        pruned.AddRange(kept.Skip(MaxKnownDevices).Select(r => r.Key));
        kept = [.. kept.Take(MaxKnownDevices)];
        changed |= pruned.Count > 0;

        foreach (var row in kept.Where(r => !liveKeys.Contains(r.Key)).OrderBy(r => r.Flow).ThenBy(r => r.FriendlyName, StringComparer.OrdinalIgnoreCase))
        {
            var endpoint = new AudioEndpoint(row.LastEndpointId, row.FriendlyName, row.DeviceDescription, row.Flow, EndpointState.NotPresent, false, false, row.ContainerId, row.IsBluetooth);
            devices.Add((endpoint, row.Key, false, row.IsBluetooth, null, null));
        }

        return new BuildResult(devices, idToKey, kept, changed, pruned);
    }

    private void ApplyBuild(BuildResult built)
    {
        MigrateKeys(built.IdToKey);
        if (built.KnownChanged)
        {
            Settings.KnownDevices = built.Known;
            foreach (var key in built.Pruned)
            {
                PurgeKey(key);
            }

            QueueSave();
        }

        var existing = _allCards.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        _allCards.Clear();
        foreach (var (endpoint, key, connected, bluetooth, volume, muted) in built.Devices.DistinctBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
        {
            var config = ConfigFor(key, endpoint.Id);
            if (existing.TryGetValue(key, out var card))
            {
                card.Refresh(endpoint, connected, bluetooth, volume, muted, config);
            }
            else
            {
                card = new DeviceCardViewModel(this, key, endpoint, config);
                card.Refresh(endpoint, connected, bluetooth, volume ?? 0, muted ?? false, config);
            }

            _allCards.Add(card);
        }

        SeedQuickPins();
        ReconcileApps();
        _ = ApplyWaveLinkVisualsAsync();
    }

    private void MigrateKeys(IReadOnlyDictionary<string, string> idToKey)
    {
        var s = Settings;
        var changed = DeviceKeyStore.ReKeyList(s.DeviceOrder, idToKey);
        foreach (var group in s.DeviceGroups)
        {
            changed |= DeviceKeyStore.ReKeyList(group.MemberIds, idToKey);
        }

        changed |= DeviceKeyStore.ReKeyMap(s.Devices, idToKey);
        if (s.SettingsSchemaVersion < AppSettings.DeviceKeySchemaVersion)
        {
            s.SettingsSchemaVersion = AppSettings.DeviceKeySchemaVersion;
            _logger.LogInformation("Migrated device stores to device keys (schema v{Version})", AppSettings.DeviceKeySchemaVersion);
            changed = true;
        }

        if (changed)
        {
            QueueSave();
        }
    }

    private void SeedQuickPins()
    {
        var s = Settings;
        if (s.SettingsSchemaVersion >= AppSettings.QuickControlsSeedSchemaVersion)
        {
            return;
        }

        if (!s.Devices.Values.Any(d => d.PinnedToQuickControls == true))
        {
            foreach (var card in _allCards.Where(c => c.IsDefault || c.IsDefaultCommunications))
            {
                var cfg = Clone(card.Config);
                cfg.PinnedToQuickControls = true;
                card.ApplyConfig(cfg);
                SaveConfig(card);
            }
        }

        s.SettingsSchemaVersion = AppSettings.QuickControlsSeedSchemaVersion;
        QueueSave();
    }

    private RuleSnapshot Snapshot()
    {
        var endpoints = Endpoints.GetEndpoints(EndpointFlow.Render).Concat(Endpoints.GetEndpoints(EndpointFlow.Capture)).ToList();
        return new RuleSnapshot(_rules.Rules, endpoints, _sessions.GetSessions());
    }

    private void SyncBlocks()
    {
        var cardByKey = _allCards.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        var ordered = ComputeOrderedBlockIds(out var groupByMember);
        foreach (var card in _allCards)
        {
            card.IsGroupMember = groupByMember.ContainsKey(card.Key);
        }

        var desired = new List<object>();
        var desiredMembers = new Dictionary<DeviceGroupViewModel, List<DeviceCardViewModel>>();
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ordered)
        {
            if (Settings.DeviceGroups.FirstOrDefault(g => SameId(g.Id, id)) is { } record)
            {
                var members = record.MemberIds.Where(cardByKey.ContainsKey).Select(k => cardByKey[k]).Distinct().Where(IsListed).ToList();
                if (members.Count == 0)
                {
                    continue;
                }

                if (!_groups.TryGetValue(id, out var group))
                {
                    group = _groups[id] = new DeviceGroupViewModel(this, record.Id, record.Title);
                }
                else if (!group.IsEditing)
                {
                    group.Title = record.Title;
                }

                live.Add(id);
                desired.Add(group);
                desiredMembers[group] = members;
            }
            else if (cardByKey.TryGetValue(id, out var card) && IsListed(card))
            {
                desired.Add(card);
            }
        }

        foreach (var group in _groups.Values)
        {
            Reconcile(group.Members, desiredMembers.GetValueOrDefault(group) ?? [], removeOnly: true);
        }

        Reconcile(Blocks, desired, removeOnly: true);
        foreach (var gone in _groups.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _groups.Remove(gone);
        }

        Reconcile(Blocks, desired, removeOnly: false);
        foreach (var (group, members) in desiredMembers)
        {
            Reconcile(group.Members, members, removeOnly: false);
        }

        _visibleCards.Clear();
        foreach (var block in Blocks)
        {
            if (block is DeviceCardViewModel c)
            {
                _visibleCards.Add(c);
            }
            else if (block is DeviceGroupViewModel g)
            {
                _visibleCards.AddRange(g.Members);
            }
        }

        IsInitializing = false;
        Raise(nameof(ShowEmptyState));
        Raise(nameof(ShowFilteredEmptyState));
    }

    private static void Reconcile<T>(IList<T> target, IReadOnlyList<T> desired, bool removeOnly)
        where T : class
    {
        if (removeOnly)
        {
            var wanted = new HashSet<T>(desired);
            var seen = new HashSet<T>();
            for (var i = target.Count - 1; i >= 0; i--)
            {
                if (!wanted.Contains(target[i]) || !seen.Add(target[i]))
                {
                    target.RemoveAt(i);
                }
            }

            return;
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var current = target.IndexOf(desired[i]);
            if (current == i)
            {
                continue;
            }

            if (current >= 0)
            {
                target.RemoveAt(current);
            }

            target.Insert(Math.Min(i, target.Count), desired[i]);
        }
    }

    private bool IsListed(DeviceCardViewModel card) =>
        DeviceListFilter.IsListed(card.IsGroupMember, card.IsEffectivelyHidden, card.IsConnected, ShowHiddenDevices, ShowDisconnectedDevices);

    private List<string> ComputeOrderedBlockIds(out Dictionary<string, string> groupByMember)
    {
        var present = new HashSet<string>(_allCards.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);
        groupByMember = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Settings.DeviceGroups.Where(g => g.MemberIds.Count(present.Contains) >= 2))
        {
            foreach (var key in group.MemberIds.Where(present.Contains))
            {
                groupByMember[key] = group.Id;
            }
        }

        var defaults = new List<string>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in _allCards)
        {
            var id = groupByMember.TryGetValue(card.Key, out var gid) ? gid : card.Key;
            if (emitted.Add(id))
            {
                defaults.Add(id);
            }
        }

        return MergeOrder(defaults, Settings.DeviceOrder);
    }

    private static List<string> MergeOrder(List<string> defaults, List<string> manual)
    {
        var valid = new HashSet<string>(defaults, StringComparer.OrdinalIgnoreCase);
        var result = manual.Where(valid.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 0; i < defaults.Count; i++)
        {
            var id = defaults[i];
            if (result.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var at = -1;
            for (var p = i - 1; p >= 0 && at < 0; p--)
            {
                var idx = result.FindIndex(x => SameId(x, defaults[p]));
                at = idx >= 0 ? idx + 1 : -1;
            }

            for (var n = i + 1; n < defaults.Count && at < 0; n++)
            {
                at = result.FindIndex(x => SameId(x, defaults[n]));
            }

            result.Insert(at < 0 ? result.Count : at, id);
        }

        return result;
    }

    private void OnSettingsChanged()
    {
        foreach (var card in _allCards)
        {
            var config = ConfigFor(card.Key, card.Endpoint.Id);
            if (!SameConfig(config, card.Config))
            {
                card.ApplyConfig(config);
            }
            else
            {
                card.RefreshOptions();
            }
        }

        RaiseAll();
        var signature = AppsSignature();
        if (signature != _appsSignature)
        {
            _appsSignature = signature;
            RefreshHiddenApps();
            QueueApps();
        }

        if (Settings.WaveLinkChannelStyle != _visualStyle)
        {
            _ = ApplyWaveLinkVisualsAsync();
        }
    }

    private string AppsSignature()
    {
        var s = Settings;
        return $"{s.FilterAudioForwarders}|{s.ShowAppIndicators}|{s.AlwaysShowPinnedApps}|{s.ShowNowPlaying}|{s.HiddenApps.Count}|{s.HiddenAppsOnDevice.Count}|{string.Join(',', s.HiddenApps.Select(h => h.Key))}";
    }

    private void RefreshHiddenApps()
    {
        _hiddenApps.Clear();
        _hiddenApps.UnionWith(Settings.HiddenApps.Select(h => h.Key).Where(k => k.Length > 0));
        _hiddenAppsOnDevice.Clear();
        _hiddenAppsOnDevice.UnionWith(Settings.HiddenAppsOnDevice.Select(h => HideKey(h.Key, h.EndpointId)));
    }

    private async Task ApplyWaveLinkVisualsAsync()
    {
        var style = _visualStyle = Settings.WaveLinkChannelStyle;
        if (style == WaveLinkChannelStyle.Off || !_waveLink.IsAvailable)
        {
            foreach (var card in _allCards)
            {
                card.SetWaveLinkVisual(null, null, null);
            }

            return;
        }

        var endpoints = Endpoints.GetEndpoints(EndpointFlow.Render).Concat(Endpoints.GetEndpoints(EndpointFlow.Capture)).ToList();
        var channels = WaveLinkChannelMap.Build(_waveLink.LastSnapshot, endpoints);
        var mixes = WaveLinkChannelMap.BuildMixMap(_waveLink.LastSnapshot, endpoints);
        foreach (var card in _allCards.ToList())
        {
            if (channels.TryGetValue(card.Endpoint.Id, out var channel))
            {
                var (bitmap, accent) = await Images.WaveLinkAsync(channel.ImageData).ConfigureAwait(true);
                card.SetWaveLinkVisual(style == WaveLinkChannelStyle.Icons ? null : accent is { } a ? DeviceVisuals.NearestSwatch(a) : null, style == WaveLinkChannelStyle.Icons ? bitmap : null, null);
            }
            else if (mixes.TryGetValue(card.Endpoint.Id, out var mix))
            {
                card.SetWaveLinkVisual(Colors.White, null, DeviceVisuals.GlyphForWaveLinkIcon(mix.IconName));
            }
            else
            {
                card.SetWaveLinkVisual(null, null, null);
            }
        }
    }

    private void OnExternalMute(string endpointId, bool muted)
    {
        if (FindByEndpoint(endpointId) is not { } card)
        {
            return;
        }

        card.SyncMuted(muted);
        if (card.PinnedMuteTarget is not { } target || target == muted)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (_lastMuteToast.TryGetValue(endpointId, out var last) && now - last < ToastRateLimit)
        {
            return;
        }

        _lastMuteToast[endpointId] = now;
        _services.GetService<NotificationService>()?.Show(
            $"Earmark kept '{card.Endpoint.FriendlyName}' {(target ? "muted" : "unmuted")}",
            $"Rule \"{card.PinnedMuteRule ?? "an active rule"}\" pins this device, so the external change was reverted.");
    }

    private void ShowToast(string message) => _services.GetService<MainWindow>()?.ShowToast(message);

    private DeviceCardViewModel? FindByKey(string key) => _allCards.FirstOrDefault(c => SameId(c.Key, key));

    private DeviceCardViewModel? FindByEndpoint(string id) => _allCards.FirstOrDefault(c => c.IsConnected && SameId(c.Endpoint.Id, id));

    private DeviceConfig ConfigFor(string key, string endpointId) =>
        Settings.Devices.TryGetValue(key, out var byKey) ? Clone(byKey) : Settings.Devices.TryGetValue(endpointId, out var byId) ? Clone(byId) : new DeviceConfig();

    private void SaveConfig(DeviceCardViewModel card)
    {
        if (card.Config.IsDefault)
        {
            Settings.Devices.Remove(card.Key);
        }
        else
        {
            Settings.Devices[card.Key] = Clone(card.Config);
        }
    }

    private void PurgeKey(string key)
    {
        Settings.DeviceOrder.RemoveAll(id => SameId(id, key));
        Settings.Devices.Remove(key);
        RemoveFromGroupRecord(key);
    }

    private void RemoveFromGroupRecord(string key)
    {
        if (GroupOf(key) is not { } group)
        {
            return;
        }

        group.MemberIds.RemoveAll(id => SameId(id, key));
        if (group.MemberIds.Count < 2)
        {
            Settings.DeviceGroups.Remove(group);
            var at = Settings.DeviceOrder.FindIndex(id => SameId(id, group.Id));
            if (at >= 0)
            {
                Settings.DeviceOrder.RemoveAt(at);
                Settings.DeviceOrder.InsertRange(at, group.MemberIds);
            }
        }
    }

    private DeviceGroup? GroupOf(string key) => Settings.DeviceGroups.FirstOrDefault(g => g.MemberIds.Any(id => SameId(id, key)));

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string HideKey(string key, string endpointId) => key + "\0" + endpointId;

    private static KnownDevice Clone(KnownDevice d) => new()
    {
        Key = d.Key, LastEndpointId = d.LastEndpointId, FriendlyName = d.FriendlyName, DeviceDescription = d.DeviceDescription,
        Flow = d.Flow, ContainerId = d.ContainerId, IsBluetooth = d.IsBluetooth, LastSeenUtc = d.LastSeenUtc,
    };

    public static DeviceConfig Clone(DeviceConfig c) => new()
    {
        Hidden = c.Hidden, Pinned = c.Pinned, PinnedToQuickControls = c.PinnedToQuickControls, VolumeControlsHidden = c.VolumeControlsHidden,
        Glyph = c.Glyph, AccentColour = c.AccentColour, ShowNowPlaying = c.ShowNowPlaying, NowPlayingFill = c.NowPlayingFill,
        ShowAppIndicators = c.ShowAppIndicators, ShowAppMeters = c.ShowAppMeters, MeterEnabled = c.MeterEnabled,
        ShowPeakIndicator = c.ShowPeakIndicator, ShowRules = c.ShowRules, ShowDeviceBadges = c.ShowDeviceBadges,
    };

    private static bool SameConfig(DeviceConfig a, DeviceConfig b) =>
        a.Hidden == b.Hidden && a.Pinned == b.Pinned && a.PinnedToQuickControls == b.PinnedToQuickControls && a.VolumeControlsHidden == b.VolumeControlsHidden
        && a.Glyph == b.Glyph && a.AccentColour == b.AccentColour && a.ShowNowPlaying == b.ShowNowPlaying && a.NowPlayingFill == b.NowPlayingFill
        && a.ShowAppIndicators == b.ShowAppIndicators && a.ShowAppMeters == b.ShowAppMeters && a.MeterEnabled == b.MeterEnabled
        && a.ShowPeakIndicator == b.ShowPeakIndicator && a.ShowRules == b.ShowRules && a.ShowDeviceBadges == b.ShowDeviceBadges;
}
