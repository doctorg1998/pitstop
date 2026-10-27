using Pitstop.Core.Config;
using Pitstop.Core.Storage;

namespace Pitstop.Tests;

public class ConfigLoaderTests
{
    private const string Path = @"C:\fake\config.json";

    [Fact]
    public void Missing_file_uses_defaults_without_warning()
    {
        var result = new ConfigLoader(new FakeFileStore()).Load(Path);

        Assert.True(result.UsedDefaults);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Config.Profiles);
    }

    [Fact]
    public void Valid_file_loads_and_ignores_unknown_fields()
    {
        var files = new FakeFileStore();
        files.Files[Path] = """
            { "schemaVersion": 1, "someFutureSetting": true,
              "profiles": [ { "id": "play", "name": "Play", "priority": 20,
                "triggers": { "processes": ["cs2.exe"] },
                "actions": { "powerPlan": "High performance", "breakReminderMinutes": 90 } } ] }
            """;

        var result = new ConfigLoader(files).Load(Path);

        Assert.False(result.UsedDefaults);
        var play = Assert.Single(result.Config.Profiles);
        Assert.Equal("High performance", play.Actions.PowerPlan);
        Assert.Empty(play.Actions.CloseApps); // missing field falls back to default
    }

    [Fact]
    public void Corrupt_json_backs_up_file_and_uses_defaults()
    {
        var files = new FakeFileStore();
        files.Files[Path] = "{ not json";

        var result = new ConfigLoader(files).Load(Path);

        Assert.True(result.UsedDefaults);
        Assert.Single(result.Warnings);
        Assert.Equal("{ not json", files.Files[Path + ".bad"]);
        // BUG-009: the loader mustn't claim "using defaults"; on a live reload the app keeps the previous profiles.
        Assert.DoesNotContain("default", result.Warnings[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("config.json.bad", result.Warnings[0]);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 99 }""", "schemaVersion")]
    [InlineData("""{ "profiles": [ { "id": "a" }, { "id": "A" } ] }""", "Duplicate")]
    [InlineData("""{ "profiles": [ { "id": "a", "actions": { "closeApps": ["explorer.exe"] } } ] }""", "protected")]
    [InlineData("""{ "profiles": [ { "id": "a", "actions": { "breakReminderMinutes": 0 } } ] }""", "breakReminderMinutes")]
    [InlineData("""{ "profiles": [ { "id": "a", "triggers": { "hotkey": "P" } } ] }""", "isn't valid")]
    [InlineData("""{ "profiles": [ { "id": "a", "triggers": { "hotkey": "Ctrl+Alt+P" } }, { "id": "b", "triggers": { "hotkey": "alt+ctrl+p" } } ] }""", "already used")]
    public void Invalid_config_falls_back_with_reason(string json, string expectedInWarning)
    {
        var files = new FakeFileStore();
        files.Files[Path] = json;

        var result = new ConfigLoader(files).Load(Path);

        Assert.True(result.UsedDefaults);
        Assert.Contains(expectedInWarning, result.Warnings[0]);
    }

    [Fact]
    public void Starter_config_is_valid() =>
        Assert.Empty(ConfigLoader.Validate(ConfigLoader.CreateStarter()));

    [Fact]
    public void Save_then_load_round_trips()
    {
        var files = new FakeFileStore();
        var loader = new ConfigLoader(files);
        var config = new PitstopConfig();
        config.Profiles.Add(new ProfileDefinition { Id = "work", Name = "Work", Priority = 10 });

        loader.Save(Path, config);
        var result = loader.Load(Path);

        Assert.False(result.UsedDefaults);
        Assert.Equal("Work", Assert.Single(result.Config.Profiles).Name);
    }
}
