using System.Text.RegularExpressions;

using Earmark.App.Settings;
using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Services;
using Earmark.Core.WaveLink;

using Microsoft.Extensions.Logging;

namespace Earmark.App.Services;

/// <inheritdoc cref="IDeviceDefaultsService"/>
public sealed class DeviceDefaultsService : IDeviceDefaultsService
{
    private const string DefaultDevicesGroupTitle = "Default Devices";
    private const string WaveLinkChannelsGroupTitle = "Wave Link Channels";

    private const string DefaultVolumesRuleName = "Set default volumes";
    private const string AppOutputRuleName = "Set output device for app";

    // Covers new + classic Teams, Discord stable/Canary/PTB, and Slack. Matched case-insensitively
    // against both the process name and the full executable path.
    private const string AppOutputAppPattern = @"(ms-teams|Teams|[Dd]iscord(Canary|PTB)?|slack)\.exe";
    private const string AppOutputDevicePattern = "Comm";

    private readonly ISettingsService _settings;
    private readonly IAudioEndpointService _endpoints;
    private readonly IWaveLinkService _waveLink;
    private readonly IRulesService _rules;
    private readonly ILogger<DeviceDefaultsService> _logger;

    public DeviceDefaultsService(
        ISettingsService settings,
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
        // Seed only a blank-slate install. Any existing rule or Devices-page customisation means the
        // user (or a prior version) already configured Earmark, so seeding - which clears the
        // Devices-page state - would silently wipe their layout. Emptiness is the whole signal; no
        // persisted flag needed, and an existing install can never be mistaken for a fresh one.
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

    /// <summary>True when the install already carries user state - any rule, or any Devices-page
    /// customisation (groups, manual order, per-device config, or hidden app chips).</summary>
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

    /// <summary>
    /// Clears the Devices-page state (per-device config, groups, block order, hidden app chips) and
    /// rebuilds the two starter groups, pinning their members visible and floating them to the top.
    /// Returns the default-device endpoints (render + capture, default + communications) so the
    /// first-run seeder can build the example volume rule against them.
    /// </summary>
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

        // ---- Group 0: Default Devices (system default/communications render + capture, + first WL mix) ----
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

        // ---- Group 1: Wave Link channels (render/output-only virtual channels), minus group 0 ----
        var channelMap = WaveLinkChannelMap.Build(snapshot, combined);
        var group0Set = new HashSet<string>(group0Members, StringComparer.OrdinalIgnoreCase);
        var group1Members = render
            .Where(e => channelMap.ContainsKey(e.Id) && !group0Set.Contains(e.Id))
            .Select(e => e.Id)
            .ToList();
        AddGroup(s, orderHead, WaveLinkChannelsGroupTitle, group1Members);
        PinAll(s, group1Members);

        // Groups lead the block order; every other (lone) card slots into its default-sort position
        // below via HomeViewModel.ApplyManualBlockOrder. Non-grouped, no-rule, non-default devices
        // auto-hide on the next rebuild (DeviceCard.IsEffectivelyHidden) - the default-hidden logic.
        s.DeviceOrder = orderHead;

        _logger.LogInformation(
            "Default layout: {DefaultCount} default devices, {ChannelCount} Wave Link channels (WL snapshot {WlState})",
            group0Members.Count, group1Members.Count, snapshot is null ? "absent" : "present");

        return defaultDevices;
    }

    /// <summary>The render endpoint backing the first Wave Link mix (e.g. "Headphone Mix"), or null
    /// when Wave Link isn't connected or no mix maps to a present endpoint.</summary>
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

    /// <summary>Adds a group for <paramref name="memberIds"/> (>=2 members) and pushes its id onto the
    /// block-order head. Fewer than two members can't render as a group, so it's skipped (members are
    /// still pinned separately).</summary>
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

    private static void AddIfMissing(List<AudioEndpoint> list, AudioEndpoint? endpoint)
    {
        if (endpoint is null) return;
        if (list.Any(e => string.Equals(e.Id, endpoint.Id, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(endpoint);
    }

    /// <summary>Seeds the two disabled example rules on first run. Idempotent by rule name, so a
    /// re-seed (e.g. after a crash between the rules save and the settings save) never duplicates.</summary>
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
                    Type = ActionType.SetDeviceVolume,
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
                        Type = ActionType.SetApplicationOutput,
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
