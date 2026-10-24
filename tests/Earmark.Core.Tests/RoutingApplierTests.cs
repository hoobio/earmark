using AwesomeAssertions;

using Earmark.Audio.Routing;
using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Persistence;
using Earmark.Core.Services;
using Earmark.Core.WaveLink;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Earmark.Core.Tests;

public sealed class RoutingApplierTests : IDisposable
{
    private readonly FakeEndpoints _endpoints = new();
    private readonly FakeSessions _sessions = new();
    private readonly FakePolicy _policy = new();
    private readonly FakeWaveLink _waveLink = new();
    private readonly RulesService _rules = new(new InMemoryRuleStore());
    private readonly RoutingApplier _applier;

    public RoutingApplierTests()
    {
        var writer = new EndpointWriter(_endpoints, _waveLink, NullLogger<EndpointWriter>.Instance);
        _applier = new RoutingApplier(_rules, _sessions, _endpoints, writer, _policy, _waveLink, NullLogger<RoutingApplier>.Instance);
    }

    public void Dispose()
    {
        _applier.Dispose();
        _rules.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AudioEndpoint Ep(string name, EndpointFlow flow, bool isDefault = false) =>
        new($"id:{name}", name, "", flow, EndpointState.Active, isDefault, isDefault);

    private static AudioSession Session(string process, uint pid) =>
        new($"inst:{pid}", $"sid:{pid}", pid, process, $@"C:\apps\{process}.exe", process, "", "id:Speakers", SessionState.Active, false);

    private static RoutingRule MuteRule(string name, string device, bool pinned) => new()
    {
        Name = name,
        Enabled = true,
        Actions = { new RuleAction { Kind = ActionKind.DeviceMute, DevicePattern = device, Muted = true, Pinned = pinned } },
    };

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task Capture_device_condition_selects_the_branch_for_a_render_route()
    {
        _endpoints.Set(Ep("Headset", EndpointFlow.Render), Ep("Speakers", EndpointFlow.Render, isDefault: true), Ep("Studio Mic", EndpointFlow.Capture));
        _sessions.Sessions = new[] { Session("discord", 42) };
        await _rules.UpsertAsync(new RoutingRule
        {
            Name = "mic present",
            Enabled = true,
            Conditions = { new RuleCondition { Kind = ConditionKind.Device, Flow = ConditionFlow.Capture, DevicePattern = "Studio Mic" } },
            Actions =
            {
                new RuleAction { Kind = ActionKind.ApplicationDevice, Flow = EndpointFlow.Render, AppPattern = "discord", DevicePattern = "Headset" },
                new RuleAction { Kind = ActionKind.DefaultDevice, Flow = EndpointFlow.Render, DevicePattern = "Headset", SetsDefault = true },
            },
            ElseActions =
            {
                new RuleAction { Kind = ActionKind.ApplicationDevice, Flow = EndpointFlow.Render, AppPattern = "discord", DevicePattern = "Speakers" },
            },
        }, Ct);

        await _applier.ApplyAllAsync();

        _policy.AppRoutes.Should().ContainSingle().Which.Should().Be(("42", "id:Headset", EndpointFlow.Render));
        _policy.SystemDefaults.Should().Contain(("id:Headset", EndpointFlow.Render, RoleScope.Default));
    }

    [Fact]
    public async Task Capture_sessions_are_ignored_by_application_rules()
    {
        _endpoints.Set(Ep("Headset", EndpointFlow.Render), Ep("Speakers", EndpointFlow.Render, isDefault: true));
        _sessions.CaptureSessions = new[] { Session("discord", 42) };
        await _rules.UpsertAsync(new RoutingRule
        {
            Name = "discord to headset",
            Enabled = true,
            Actions = { new RuleAction { Kind = ActionKind.ApplicationDevice, Flow = EndpointFlow.Render, AppPattern = "discord", DevicePattern = "Headset" } },
        }, Ct);

        await _applier.ApplyAllAsync(force: true);

        _policy.AppRoutes.Should().BeEmpty();
    }

    [Fact]
    public async Task Editing_one_rule_re_fires_only_its_one_shots_and_reapply_re_fires_all()
    {
        _endpoints.Set(Ep("Speakers", EndpointFlow.Render), Ep("Headset", EndpointFlow.Render));
        var a = MuteRule("a", "Speakers", pinned: false);
        var b = MuteRule("b", "Headset", pinned: false);
        await _rules.UpsertAsync(a, Ct);
        await _rules.UpsertAsync(b, Ct);

        await _applier.ApplyAllAsync();
        await WaitUntil(() => _endpoints.Mutes.Count == 2);

        _endpoints.Mutes.Clear();
        await _applier.ApplyAllAsync();
        await Task.Delay(200, Ct);
        _endpoints.Mutes.Should().BeEmpty("a pass with no edge leaves one-shot actions alone");

        await _rules.UpsertAsync(new RoutingRule { Id = b.Id, Name = "b edited", Enabled = true, Actions = b.Actions }, Ct);
        await _applier.ApplyAllAsync();
        await WaitUntil(() => _endpoints.Mutes.Count >= 1);
        await Task.Delay(200, Ct);
        _endpoints.Mutes.Should().Equal("id:Headset");

        _endpoints.Mutes.Clear();
        await _applier.ApplyAllAsync(force: true);
        await WaitUntil(() => _endpoints.Mutes.Count == 2);
        _endpoints.Mutes.Should().BeEquivalentTo("id:Speakers", "id:Headset");
    }

    [Fact]
    public async Task Event_during_a_pass_queues_one_more_pass()
    {
        _endpoints.Set(Ep("Speakers", EndpointFlow.Render));
        await _rules.UpsertAsync(MuteRule("pinned", "Speakers", pinned: true), Ct);
        var release = _endpoints.BlockNextRead();

        _applier.Start();
        await WaitUntil(() => _endpoints.RenderReads == 1);
        _endpoints.RaiseDefaultsChanged();
        release.SetResult();

        await WaitUntil(() => _endpoints.RenderReads >= 2);
    }

    [Fact]
    public async Task Hung_wave_link_does_not_hold_the_apply_lock()
    {
        _endpoints.Set(Ep("Speakers", EndpointFlow.Render));
        await _rules.UpsertAsync(new RoutingRule
        {
            Name = "mix",
            Enabled = true,
            Actions = { new RuleAction { Kind = ActionKind.WaveLinkMix, MixPattern = "Stream", DevicePattern = "Speakers" } },
        }, Ct);
        _waveLink.HangSnapshot = true;

        _applier.Start();
        await WaitUntil(() => _waveLink.SnapshotCalls == 1);
        _endpoints.RaiseDefaultsChanged();

        await WaitUntil(() => _endpoints.RenderReads >= 2);
    }

    private sealed class InMemoryRuleStore : IRuleStore
    {
        private List<RoutingRule> _rules = new();
        public bool IsSaveBlocked => false;
        public bool IsCorrupt => false;
        public string? CorruptCopyPath => null;
        public ValueTask<IReadOnlyList<RoutingRule>> LoadAsync(CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<RoutingRule>>(_rules);
        public ValueTask SaveAsync(IEnumerable<RoutingRule> rules, CancellationToken ct = default)
        {
            _rules = rules.ToList();
            return ValueTask.CompletedTask;
        }
        public void UnblockSaves() { }
    }

    private sealed class FakeEndpoints : IAudioEndpointService
    {
        private IReadOnlyList<AudioEndpoint> _all = Array.Empty<AudioEndpoint>();
        private TaskCompletionSource? _block;
        private int _renderReads;

        public List<string> Mutes { get; } = new();
        public int RenderReads => Volatile.Read(ref _renderReads);

        public void Set(params AudioEndpoint[] endpoints) => _all = endpoints;

        public TaskCompletionSource BlockNextRead() => _block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RaiseDefaultsChanged() => DefaultsChanged?.Invoke(this, EventArgs.Empty);

        public IReadOnlyList<AudioEndpoint> GetEndpoints(EndpointFlow flow = EndpointFlow.Render)
        {
            if (flow == EndpointFlow.Render)
            {
                Interlocked.Increment(ref _renderReads);
                Interlocked.Exchange(ref _block, null)?.Task.Wait();
            }
            return _all.Where(e => e.Flow == flow).ToList();
        }

        public bool SetMuted(string id, bool muted)
        {
            lock (Mutes) Mutes.Add(id);
            return true;
        }

        public bool SetVolume(string id, float level) => true;
        public AudioEndpoint? GetById(string id) => _all.FirstOrDefault(e => e.Id == id);
        public float? GetVolume(string id) => 1f;
        public bool? GetMuted(string id) => false;
        public void PlayTestPing(string id) { }
        public float? GetPeakLevel(string id) => 0f;
        public EndpointChannelPeaks? GetChannelPeaks(string id) => null;

        public event EventHandler? EndpointsChanged { add { } remove { } }
        public event EventHandler? DefaultsChanged;
        public event EventHandler<EndpointMuteChangedEventArgs>? ExternalMuteChanged { add { } remove { } }
        public event EventHandler<EndpointVolumeChangedEventArgs>? ExternalVolumeChanged { add { } remove { } }
    }

    private sealed class FakeSessions : IAudioSessionService
    {
        public IReadOnlyList<AudioSession> Sessions { get; set; } = Array.Empty<AudioSession>();
        public IReadOnlyList<AudioSession> CaptureSessions { get; set; } = Array.Empty<AudioSession>();
        public IReadOnlyList<AudioSession> GetSessions() => Sessions;
        public IReadOnlyList<AudioSession> GetCaptureSessions() => CaptureSessions;
        public event EventHandler? CaptureSessionsChanged { add { } remove { } }
        public event EventHandler<AudioSessionEvent>? SessionAdded { add { } remove { } }
        public event EventHandler<AudioSessionRemovedEvent>? SessionRemoved { add { } remove { } }
        public event EventHandler? SessionsChanged { add { } remove { } }
    }

    private sealed class FakePolicy : IAudioPolicyService
    {
        public List<(string Pid, string EndpointId, EndpointFlow Flow)> AppRoutes { get; } = new();
        public List<(string EndpointId, EndpointFlow Flow, RoleScope Role)> SystemDefaults { get; } = new();

        public void SetDefaultEndpointForApp(string sessionIdentifier, string endpointId, RoleScope role, EndpointFlow flow) =>
            AppRoutes.Add((sessionIdentifier, endpointId, flow));

        public bool SetSystemDefaultEndpoint(string endpointId, EndpointFlow flow, RoleScope role)
        {
            SystemDefaults.Add((endpointId, flow, role));
            return true;
        }
    }

    private sealed class FakeWaveLink : IWaveLinkService
    {
        private int _snapshotCalls;

        public bool HangSnapshot { get; set; }
        public int SnapshotCalls => Volatile.Read(ref _snapshotCalls);
        public bool IsEnabled { get; set; }
        public WaveLinkConnectionState State => WaveLinkConnectionState.Disabled;
        public bool IsAvailable => false;
        public WaveLinkSnapshot? LastSnapshot => null;

        public async Task<WaveLinkSnapshot?> GetSnapshotAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _snapshotCalls);
            if (HangSnapshot)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            return null;
        }

        public Task<bool> SetMixForOutputAsync(string deviceId, string outputId, string mixId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> SetMixMutedAsync(string mixId, bool muted, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> SetMixLevelAsync(string mixId, float level, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> SetInputDeviceMutedAsync(string deviceId, string inputId, bool muted, CancellationToken ct = default) => Task.FromResult(false);

        public event EventHandler? StateChanged { add { } remove { } }
        public event EventHandler? SnapshotChanged { add { } remove { } }
    }
}
