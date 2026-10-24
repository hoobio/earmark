using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

using Earmark.Audio;
using Earmark.Core;
using Earmark.Core.Audio;
using Earmark.Core.Logging;
using Earmark.Core.Models;
using Earmark.Core.Services;
using Earmark.Core.Settings;
using Earmark.Core.WaveLink;
using Earmark.Desktop.Hosting;
using Earmark.Desktop.Styles;
using Earmark.Desktop.Views;

using Microsoft.Extensions.DependencyInjection;

[assembly: AssemblyFixture(typeof(Earmark.Desktop.Tests.AvaloniaSession))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Earmark.Desktop.Tests;

public sealed class AvaloniaSession : IDisposable
{
    private readonly HeadlessUnitTestSession _session = HeadlessUnitTestSession.StartNew(typeof(AvaloniaSession));
    private readonly string _root = Directory.CreateTempSubdirectory("earmark-desktop-tests-").FullName;

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseEarmarkFonts()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public ServiceProvider NewServices()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"))).FullName;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_ => new FileLoggerProvider(Path.Combine(folder, "test.log")));
        services.AddEarmarkCore(Path.Combine(folder, "rules.json"), Path.Combine(folder, "settings.json"));
        services.AddEarmarkInterop();
        services.AddUpdates();
        var sessions = new SampleSessions();
        services.AddSingleton<IAudioEndpointService>(new SampleEndpoints());
        services.AddSingleton<IAudioSessionService>(sessions);
        services.AddSingleton<IAudioSessionMeterService>(sessions);
        services.AddSingleton<IRunningProcessProvider>(new SampleProcesses());
        services.AddSingleton<INowPlayingService>(new SampleNowPlaying());
        services.AddSingleton<IWaveLinkService>(new NoWaveLink());
        services.AddSingleton<IBluetoothAudioControl>(new NoBluetooth());
        services.AddSingleton<IAudioPolicyService>(new NoPolicy());
        services.AddSingleton<MainWindow>();
        services.AddDevicesArea().AddRulesArea().AddSessionsArea().AddSettingsArea().AddQuickControlsArea();
        var provider = services.BuildServiceProvider();
        Seed(provider);
        return provider;
    }

    public void Run(Action action) => _session.Dispatch(action, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    public static void WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Assert.True(condition(), what);
    }

    public void Dispose()
    {
        _session.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static void Seed(ServiceProvider provider)
    {
        var settings = provider.GetRequiredService<SettingsService>().Current;
        settings.DeviceGroups.Add(new DeviceGroup { Id = "desk", Title = "Desk", MemberIds = [DeviceIdentity.KeyFor(SampleAudio.Headphones), DeviceIdentity.KeyFor(SampleAudio.Speakers)] });
        settings.Devices[DeviceIdentity.KeyFor(SampleAudio.Headphones)] = new DeviceConfig { PinnedToQuickControls = true };
        settings.Devices[DeviceIdentity.KeyFor(SampleAudio.Speakers)] = new DeviceConfig { PinnedToQuickControls = true };

        var rules = provider.GetRequiredService<IRulesService>();
        foreach (var (name, app) in new[] { ("Music to headphones", "spotify"), ("Chat to headphones", "discord") })
        {
            rules.UpsertAsync(new RoutingRule
            {
                Name = name,
                Conditions = [new RuleCondition { Kind = ConditionKind.Device, DevicePattern = "Headphones" }],
                Actions = [new RuleAction { Kind = ActionKind.ApplicationDevice, AppPattern = app, DevicePattern = "Headphones" }],
                ElseActions = [new RuleAction { Kind = ActionKind.DefaultDevice, DevicePattern = "Speakers" }],
            }).GetAwaiter().GetResult();
        }
    }
}
