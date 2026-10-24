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
