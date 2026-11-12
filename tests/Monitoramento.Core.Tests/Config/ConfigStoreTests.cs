using Monitoramento.Core.Config;
using Monitoramento.Core.Models;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Config;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string ConfigPath => _temp.Combine("sub", "config.json");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Load_MissingFileReturnsDefaults()
    {
        var store = new ConfigStore(ConfigPath);

        var config = store.Load();

        Assert.Empty(config.Favorites);
        Assert.Empty(config.Saved);
        Assert.Equal(2000, config.Settings.RefreshIntervalMs);
        Assert.True(config.Settings.MinimizeToTray);
        Assert.Equal("system", config.Settings.Theme);
        Assert.Null(store.LastLoadError);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsEverything()
    {
        var saved = new SavedProcess
        {
            Id = "6f1c2e0a-0000-0000-0000-000000000001",
            Name = "API",
            Command = "npm run dev",
            WorkingDirectory = @"C:\src\api",
            Env = new Dictionary<string, string> { ["PORT"] = "3000" },
            LaunchMode = LaunchModes.Hidden,
            Notes = "anotação",
        };
        var config = new AppConfig
        {
            Favorites = ["app:code.exe", "cmd:8096f678a352"],
            Saved = [saved],
            Settings = new Settings { RefreshIntervalMs = 5000, MinimizeToTray = false, Theme = "dark" },
        };

        new ConfigStore(ConfigPath).Save(config);
        var loaded = new ConfigStore(ConfigPath).Load();

        Assert.Equal(config.Favorites, loaded.Favorites);
        Assert.Equal(config.Settings, loaded.Settings);
        var item = Assert.Single(loaded.Saved);
        Assert.Equal(saved.Id, item.Id);
        Assert.Equal(saved.Command, item.Command);
        Assert.Equal(saved.WorkingDirectory, item.WorkingDirectory);
        Assert.Equal(LaunchModes.Hidden, item.LaunchMode);
        Assert.Equal("anotação", item.Notes);
        Assert.Equal("3000", item.Env["PORT"]);
    }

    [Fact]
    public void Save_IsAtomicAndLeavesNoTempFile()
    {
        var store = new ConfigStore(ConfigPath);

        store.Save(new AppConfig { Favorites = ["app:a.exe"] });
        store.Save(new AppConfig { Favorites = ["app:b.exe"] });

        Assert.Equal(["config.json"], Directory.GetFiles(Path.GetDirectoryName(ConfigPath)!).Select(Path.GetFileName));
        Assert.Equal(["app:b.exe"], new ConfigStore(ConfigPath).Load().Favorites);
    }

    [Fact]
    public void Load_CorruptedFileIsBackedUpAndDefaultsAreUsed()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, "{ isto não é json");
        var store = new ConfigStore(ConfigPath);

        var config = store.Load();

        Assert.Empty(config.Favorites);
        Assert.NotNull(store.LastLoadError);
        Assert.Equal("{ isto não é json", File.ReadAllText(ConfigPath + ".bak"));
    }

    [Fact]
    public void Load_InvalidValuesFallBackToDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(
            ConfigPath,
            """{ "favorites": null, "saved": [ { "id": "", "name": "x", "command": "" } ], "settings": { "refreshIntervalMs": 10, "theme": "rosa" } }""");

        var config = new ConfigStore(ConfigPath).Load();

        Assert.Empty(config.Favorites);
        Assert.Empty(config.Saved);
        Assert.Equal(2000, config.Settings.RefreshIntervalMs);
        Assert.Equal("system", config.Settings.Theme);
    }

    [Theory]
    [InlineData(2000, "system", true)]
    [InlineData(500, "dark", true)]
    [InlineData(60000, "light", true)]
    [InlineData(100, "dark", false)]
    [InlineData(2000, "rosa", false)]
    public void SettingsValidator_ChecksRangeAndTheme(int interval, string theme, bool valid)
    {
        var error = SettingsValidator.Validate(new Settings { RefreshIntervalMs = interval, Theme = theme });

        Assert.Equal(valid, error is null);
    }
}
