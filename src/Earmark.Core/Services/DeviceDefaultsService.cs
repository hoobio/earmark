using System.Text.RegularExpressions;

using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Settings;
using Earmark.Core.WaveLink;

using Microsoft.Extensions.Logging;

namespace Earmark.Core.Services;

public sealed class DeviceDefaultsService
{
    private const string DefaultDevicesGroupTitle = "Default Devices";
    private const string WaveLinkChannelsGroupTitle = "Wave Link Channels";

    private const string DefaultVolumesRuleName = "Set default volumes";
    private const string AppOutputRuleName = "Set output device for app";

    private const string AppOutputAppPattern = @"(ms-teams|Teams|[Dd]iscord(Canary|PTB)?|slack)\.exe";
    private const string AppOutputDevicePattern = "Comm";

    private readonly SettingsService _settings;
    private readonly IAudioEndpointService _endpoints;
    private readonly IWaveLinkService _waveLink;
    private readonly IRulesService _rules;
    private readonly ILogger<DeviceDefaultsService> _logger;

    public DeviceDefaultsService(
        SettingsService settings,
        IAudioEndpointService endpoints,
        IWaveLinkService waveLink,
        IRulesService rules,
        ILogger<DeviceDefaultsService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _waveLink = waveLink ?? throw new ArgumentNullException(nameof(waveLink));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event EventHandler? DefaultsApplied;

    public async Task SeedDefaultsIfEmptyAsync(CancellationToken ct = default)
    {
        // Seeding clears the Devices-page state, so only a blank-slate install may be seeded; emptiness is the fresh-install signal.
        if (HasExistingConfig())
        {
            _logger.LogInformation("Existing config present; skipping new-install seeding");
            return;
        }

        _logger.LogInformation("Empty config; seeding new-install defaults");
        var defaultDevices = ApplyDefaultDeviceLayout();
        await SeedExampleRulesAsync(defaultDevices, ct).ConfigureAwait(false);
        await SaveAndNotifyAsync(ct).ConfigureAwait(false);
    }

    private bool HasExistingConfig()
    {
        var s = _settings.Current;
        return _rules.Rules.Count > 0
            || s.DeviceGroups.Count > 0
            || s.DeviceOrder.Count > 0
            || s.Devices.Count > 0
            || s.HiddenApps.Count > 0;
    }

    public async Task ResetDeviceLayoutAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Resetting Devices page to defaults");
        ApplyDefaultDeviceLayout();
        await SaveAndNotifyAsync(ct).ConfigureAwait(false);
    }

    private List<AudioEndpoint> ApplyDefaultDeviceLayout()
    {
        var s = _settings.Current;
        s.Devices.Clear();
        s.DeviceGroups.Clear();
        s.DeviceOrder.Clear();
        s.HiddenApps.Clear();

        var render = _endpoints.GetEndpoints(EndpointFlow.Render).Where(e => e.State == EndpointState.Active).ToList();
        var capture = _endpoints.GetEndpoints(EndpointFlow.Capture).Where(e => e.State == EndpointState.Active).ToList();
        var combined = render.Concat(capture).ToList();
        var snapshot = _waveLink.LastSnapshot;

        var defaultDevices = new List<AudioEndpoint>();
        AddIfMissing(defaultDevices, render.FirstOrDefault(e => e.IsDefault));
        AddIfMissing(defaultDevices, render.FirstOrDefault(e => e.IsDefaultCommunications));
        AddIfMissing(defaultDevices, capture.FirstOrDefault(e => e.IsDefault));
        AddIfMissing(defaultDevices, capture.FirstOrDefault(e => e.IsDefaultCommunications));

        var group0Members = defaultDevices.Select(e => e.Id).ToList();

        var mixEndpointId = FindFirstMixEndpointId(snapshot, combined);
        if (mixEndpointId is not null &&
            !group0Members.Contains(mixEndpointId, StringComparer.OrdinalIgnoreCase))
        {
            group0Members.Add(mixEndpointId);
        }

        var orderHead = new List<string>();
        AddGroup(s, orderHead, DefaultDevicesGroupTitle, group0Members);
        PinAll(s, group0Members);
        QuickPinAll(s, group0Members);

        var channelMap = WaveLinkChannelMap.Build(snapshot, combined);
        var group0Set = new HashSet<string>(group0Members, StringComparer.OrdinalIgnoreCase);
        var group1Members = render
            .Where(e => channelMap.ContainsKey(e.Id) && !group0Set.Contains(e.Id))
            .Select(e => e.Id)
            .ToList();
        AddGroup(s, orderHead, WaveLinkChannelsGroupTitle, group1Members);
        PinAll(s, group1Members);

        // Only groups lead DeviceOrder; lone cards fall into default-sort position via HomeViewModel.ApplyManualBlockOrder.
        s.DeviceOrder = orderHead;

        _logger.LogInformation(
            "Default layout: {DefaultCount} default devices, {ChannelCount} Wave Link channels (WL snapshot {WlState})",
            group0Members.Count, group1Members.Count, snapshot is null ? "absent" : "present");

        return defaultDevices;
    }

    private static string? FindFirstMixEndpointId(WaveLinkSnapshot? snapshot, IReadOnlyList<AudioEndpoint> combined)
    {
        if (snapshot is null) return null;
        var mixMap = WaveLinkChannelMap.BuildMixMap(snapshot, combined);
        foreach (var mix in snapshot.Mixes)
        {
            foreach (var pair in mixMap)
            {
                if (string.Equals(pair.Value.Id, mix.Id, StringComparison.Ordinal)) return pair.Key;
            }
        }
        return null;
    }

    private static void AddGroup(AppSettings s, List<string> orderHead, string title, List<string> memberIds)
    {
        if (memberIds.Count < 2) return;
        var group = new DeviceGroup
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            MemberIds = memberIds,
        };
        s.DeviceGroups.Add(group);
        orderHead.Add(group.Id);
    }

    private static void PinAll(AppSettings s, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            s.Devices[id] = new DeviceConfig { Pinned = true };
        }
    }

    private static void QuickPinAll(AppSettings s, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (!s.Devices.TryGetValue(id, out var cfg))
            {
                cfg = new DeviceConfig();
                s.Devices[id] = cfg;
            }
            cfg.PinnedToQuickControls = true;
        }
    }

    private static void AddIfMissing(List<AudioEndpoint> list, AudioEndpoint? endpoint)
    {
        if (endpoint is null) return;
        if (list.Any(e => string.Equals(e.Id, endpoint.Id, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(endpoint);
    }

    // Idempotent by rule name: a crash between the rules save and the settings save re-seeds on next start.
    private async Task SeedExampleRulesAsync(List<AudioEndpoint> defaultDevices, CancellationToken ct)
    {
        if (defaultDevices.Count > 0 && !RuleNameExists(DefaultVolumesRuleName))
        {
            var volumeRule = new RoutingRule
            {
                Name = DefaultVolumesRuleName,
                Enabled = false,
                Actions = defaultDevices.Select(e => new RuleAction
                {
                    Kind = ActionKind.DeviceVolume,
                    DevicePattern = $"^{Regex.Escape(e.FriendlyName)}$",
                    Volume = 1f,
                }).ToList(),
            };
            await _rules.UpsertAsync(volumeRule, ct).ConfigureAwait(false);
        }

        if (!RuleNameExists(AppOutputRuleName))
        {
            var appRule = new RoutingRule
            {
                Name = AppOutputRuleName,
                Enabled = false,
                Actions =
                {
                    new RuleAction
                    {
                        Kind = ActionKind.ApplicationDevice,
                        Flow = EndpointFlow.Render,
                        AppPattern = AppOutputAppPattern,
                        DevicePattern = AppOutputDevicePattern,
                    },
                },
            };
            await _rules.UpsertAsync(appRule, ct).ConfigureAwait(false);
        }
    }

    private bool RuleNameExists(string name) =>
        _rules.Rules.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    private async Task SaveAndNotifyAsync(CancellationToken ct)
    {
        await _settings.SaveAsync(ct).ConfigureAwait(false);
        DefaultsApplied?.Invoke(this, EventArgs.Empty);
    }
}
