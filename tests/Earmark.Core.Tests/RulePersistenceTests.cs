using System.Text.Json;

using AwesomeAssertions;

using Earmark.Core.Models;
using Earmark.Core.Persistence;
using Earmark.Core.Services;
using Earmark.Core.Settings;

using Xunit;

namespace Earmark.Core.Tests;

public sealed class RulePersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "earmark-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string RulesPath => Path.Combine(_dir, "rules.json");

    public RulePersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static RoutingRule Rule(string name) => new() { Name = name };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public async Task Save_round_trips_and_leaves_no_temp_file()
    {
        using var store = new JsonRuleStore(RulesPath);
        await store.SaveAsync(new[] { Rule("a"), Rule("b") }, Ct);
        await store.SaveAsync(new[] { Rule("c") }, Ct);

        var loaded = await store.LoadAsync(Ct);

        loaded.Select(r => r.Name).Should().Equal("c");
        Directory.GetFiles(_dir).Select(Path.GetFileName).Should().Equal("rules.json");
    }

    [Fact]
    public async Task Corrupt_file_is_copied_aside_and_never_overwritten()
    {
        const string garbage = "[{\"name\": \"torn";
        await File.WriteAllTextAsync(RulesPath, garbage, Ct);
        using var store = new JsonRuleStore(RulesPath);
        using var rules = new RulesService(store);

        var load = () => rules.LoadAsync(Ct);
        await load.Should().ThrowAsync<JsonException>();

        rules.IsSaveBlocked.Should().BeTrue();
        rules.IsCorrupt.Should().BeTrue();
        rules.CorruptCopyPath.Should().Be(store.CorruptCopyPath).And.NotBeNull();
        Path.GetFileName(store.CorruptCopyPath!).Should().StartWith("rules.corrupt-").And.EndWith(".json");
        (await File.ReadAllTextAsync(store.CorruptCopyPath!, Ct)).Should().Be(garbage);

        var upsert = () => rules.UpsertAsync(Rule("new"), Ct);
        await upsert.Should().ThrowAsync<InvalidOperationException>();
        rules.Rules.Should().BeEmpty();
        (await File.ReadAllTextAsync(RulesPath, Ct)).Should().Be(garbage);
    }

    [Fact]
    public async Task Saving_resumes_after_unblock_or_a_good_load()
    {
        await File.WriteAllTextAsync(RulesPath, "not json", Ct);
        using var store = new JsonRuleStore(RulesPath);
        var load = async () => await store.LoadAsync(Ct);
        await load.Should().ThrowAsync<JsonException>();

        store.UnblockSaves();
        await store.SaveAsync(new[] { Rule("kept") }, Ct);
        (await store.LoadAsync(Ct)).Select(r => r.Name).Should().Equal("kept");

        await File.WriteAllTextAsync(RulesPath, "not json", Ct);
        await load.Should().ThrowAsync<JsonException>();
        store.IsSaveBlocked.Should().BeTrue();

        await File.WriteAllTextAsync(RulesPath, "[]", Ct);
        await store.LoadAsync(Ct);
        store.IsSaveBlocked.Should().BeFalse();
    }

    [Fact]
    public async Task Failed_corrupt_copy_still_blocks_saves_and_names_no_file()
    {
        await File.WriteAllTextAsync(RulesPath, "not json", Ct);
        var clock = new FixedClock(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        Directory.CreateDirectory(Path.Combine(_dir, "rules.corrupt-20260102-030405.json"));

        using var store = new JsonRuleStore(RulesPath, clock: clock);
        var load = async () => await store.LoadAsync(Ct);

        await load.Should().ThrowAsync<JsonException>();
        store.IsSaveBlocked.Should().BeTrue();
        store.CorruptCopyPath.Should().BeNull();
    }

    [Fact]
    public async Task Corrupt_settings_load_defaults_copy_aside_and_block_saves()
    {
        const string garbage = "{\"verboseLogging\": tr";
        var path = Path.Combine(_dir, "settings.json");
        await File.WriteAllTextAsync(path, garbage, Ct);
        using var settings = new SettingsService(path);

        var load = () => settings.LoadAsync(Ct);
        await load.Should().ThrowAsync<JsonException>();

        settings.IsSaveBlocked.Should().BeTrue();
        settings.IsCorrupt.Should().BeTrue();
        settings.Current.VerboseLogging.Should().BeFalse();
        Path.GetFileName(settings.CorruptCopyPath!).Should().StartWith("settings.corrupt-").And.EndWith(".json");
        (await File.ReadAllTextAsync(settings.CorruptCopyPath!, Ct)).Should().Be(garbage);

        settings.Current.VerboseLogging = true;
        await settings.SaveAsync(Ct);
        await settings.SaveBackupAsync("backup.v0", Ct);
        (await File.ReadAllTextAsync(path, Ct)).Should().Be(garbage);
        File.Exists(Path.Combine(_dir, "settings.backup.v0.json")).Should().BeFalse();

        settings.UnblockSaves();
        await settings.SaveAsync(Ct);
        using var reloaded = new SettingsService(path);
        await reloaded.LoadAsync(Ct);
        reloaded.Current.VerboseLogging.Should().BeTrue();
        reloaded.IsSaveBlocked.Should().BeFalse();
    }

    [Fact]
    public async Task Locked_files_block_saves_without_counting_as_corrupt()
    {
        var settingsPath = Path.Combine(_dir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, "{\"verboseLogging\": true}", Ct);
        await File.WriteAllTextAsync(RulesPath, "[]", Ct);
        using var store = new JsonRuleStore(RulesPath);
        using var settings = new SettingsService(settingsPath);

        using (File.Open(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        using (File.Open(RulesPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var loadSettings = () => settings.LoadAsync(Ct);
            var loadRules = async () => await store.LoadAsync(Ct);
            await loadSettings.Should().ThrowAsync<IOException>();
            await loadRules.Should().ThrowAsync<IOException>();
        }

        settings.IsSaveBlocked.Should().BeTrue();
        settings.IsCorrupt.Should().BeFalse();
        settings.CorruptCopyPath.Should().BeNull();
        store.IsSaveBlocked.Should().BeTrue();
        store.IsCorrupt.Should().BeFalse();
        store.CorruptCopyPath.Should().BeNull();

        await settings.SaveAsync(Ct);
        var saveRules = async () => await store.SaveAsync(new[] { Rule("new") }, Ct);
        await saveRules.Should().ThrowAsync<InvalidOperationException>();
        (await File.ReadAllTextAsync(settingsPath, Ct)).Should().Be("{\"verboseLogging\": true}");
        (await File.ReadAllTextAsync(RulesPath, Ct)).Should().Be("[]");
        Directory.GetFiles(_dir, "*.corrupt-*").Should().BeEmpty();
    }

    [Fact]
    public async Task Briefly_locked_files_load_after_a_retry()
    {
        var settingsPath = Path.Combine(_dir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, "{\"verboseLogging\": true}", Ct);
        await File.WriteAllTextAsync(RulesPath, "[{\"name\": \"kept\"}]", Ct);
        using var store = new JsonRuleStore(RulesPath);
        using var settings = new SettingsService(settingsPath);

        var settingsLock = File.Open(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var rulesLock = File.Open(RulesPath, FileMode.Open, FileAccess.Read, FileShare.None);
        _ = Task.Delay(150, Ct).ContinueWith(_ =>
        {
            settingsLock.Dispose();
            rulesLock.Dispose();
        }, Ct, TaskContinuationOptions.None, TaskScheduler.Default);

        var loadedRules = await store.LoadAsync(Ct);
        await settings.LoadAsync(Ct);

        loadedRules.Select(r => r.Name).Should().Equal("kept");
        store.IsSaveBlocked.Should().BeFalse();
        settings.Current.VerboseLogging.Should().BeTrue();
        settings.IsSaveBlocked.Should().BeFalse();
    }

    [Fact]
    public async Task Settings_save_is_atomic_and_reloads()
    {
        var path = Path.Combine(_dir, "settings.json");
        using var settings = new SettingsService(path);
        settings.Current.VerboseLogging = true;
        await settings.SaveAsync(Ct);

        using var reloaded = new SettingsService(path);
        await reloaded.LoadAsync(Ct);

        reloaded.Current.VerboseLogging.Should().BeTrue();
        File.Exists(path + ".tmp").Should().BeFalse();
    }
}
