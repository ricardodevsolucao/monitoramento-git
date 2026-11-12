using Monitoramento.Core.Config;
using Monitoramento.Core.Launcher;
using Monitoramento.Core.Models;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Config;

/// <summary>showWindow (antigo) → launchMode: ausente vira "terminal", sem olhar o showWindow.</summary>
public sealed class LaunchModeMigrationTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string ConfigPath => _temp.Combine("config.json");

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("""{ "id": "s1", "name": "a", "command": "npm run dev", "showWindow": false }""", "terminal")]
    [InlineData("""{ "id": "s1", "name": "a", "command": "npm run dev", "showWindow": true }""", "terminal")]
    [InlineData("""{ "id": "s1", "name": "a", "command": "npm run dev" }""", "terminal")]
    [InlineData("""{ "id": "s1", "name": "a", "command": "npm run dev", "launchMode": null }""", "terminal")]
    [InlineData("""{ "id": "s1", "name": "a", "command": "npm run dev", "launchMode": "Window" }""", "window")]
    [InlineData("""{ "id": "s1", "name": "a", "command": "npm run dev", "launchMode": "hidden" }""", "hidden")]
    [InlineData("""{ "id": "s1", "name": "a", "command": "npm run dev", "launchMode": "foguete" }""", "terminal")]
    public void Load_MapsLaunchMode(string savedJson, string expected)
    {
        File.WriteAllText(ConfigPath, $$"""{ "saved": [ {{savedJson}} ] }""");

        var config = new ConfigStore(ConfigPath).Load();

        Assert.Equal(expected, Assert.Single(config.Saved).LaunchMode);
    }

    [Fact]
    public void Save_WritesLaunchMode_AndNoShowWindow()
    {
        var store = new ConfigStore(ConfigPath);
        store.Save(new AppConfig { Saved = [new SavedProcess { Id = "s1", Name = "a", Command = "x", LaunchMode = LaunchModes.Window }] });

        var text = File.ReadAllText(ConfigPath);

        Assert.Contains("\"launchMode\": \"window\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("showWindow", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_TerminalModeRunsCmdSlashK()
    {
        var spec = LaunchCommandBuilder.Build(new SavedProcess { Id = "s1", Name = "a", Command = "npm run dev" }, @"C:\Windows\System32");

        Assert.Equal("\"C:\\Windows\\System32\\cmd.exe\" /s /k \"npm run dev\"", spec.CommandLine);
        Assert.True(spec.NewConsole); // sem ConPTY, cai para "window"
    }
}
