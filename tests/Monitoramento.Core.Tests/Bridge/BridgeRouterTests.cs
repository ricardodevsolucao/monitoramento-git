using System.Text.Json;
using Monitoramento.Core.Bridge;
using Monitoramento.Core.Config;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Bridge;

public sealed class BridgeRouterTests : IDisposable
{
    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    private BridgeRouter Router => new(_h.Engine);

    private async Task<JsonElement> SendAsync(string type, object? payload = null, string id = "r1")
    {
        var message = JsonSerializer.Serialize(new { id, type, payload = payload ?? new { } });
        var reply = await Router.HandleMessageAsync(message);
        return JsonDocument.Parse(reply).RootElement.Clone();
    }

    private static string Error(JsonElement reply)
    {
        Assert.False(reply.GetProperty("ok").GetBoolean());
        return reply.GetProperty("error").GetString()!;
    }

    private static JsonElement Data(JsonElement reply)
    {
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        return reply.GetProperty("data");
    }

    [Fact]
    public async Task UnknownType_ReturnsPortugueseError()
    {
        var reply = await SendAsync("voar");

        Assert.Equal("r1", reply.GetProperty("replyTo").GetString());
        Assert.Contains("desconhecid", Error(reply), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("isto não é json")]
    [InlineData("[]")]
    [InlineData("{\"id\":\"r2\"}")]
    [InlineData("")]
    public async Task MalformedMessage_ReturnsErrorWithoutThrowing(string message)
    {
        var reply = JsonDocument.Parse(await Router.HandleMessageAsync(message)).RootElement;

        Assert.False(string.IsNullOrWhiteSpace(Error(reply)));
    }

    [Fact]
    public async Task InvalidPayload_ReturnsError()
    {
        var reply = await SendAsync("toggleFavorite", new { key = 5 });

        Assert.Contains("inválid", Error(reply), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToggleFavorite_AddsRemovesAndPersists()
    {
        var added = Data(await SendAsync("toggleFavorite", new { key = "app:code.exe" }));
        Assert.Equal(["app:code.exe"], added.GetProperty("favorites").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["app:code.exe"], new ConfigStore(_h.ConfigPath).Load().Favorites);

        var removed = Data(await SendAsync("toggleFavorite", new { key = "app:code.exe" }));
        Assert.Equal(0, removed.GetProperty("favorites").GetArrayLength());
    }

    [Fact]
    public async Task ToggleFavorite_RejectsUnknownKeyFormat()
    {
        Error(await SendAsync("toggleFavorite", new { key = "qualquer" }));
    }

    [Fact]
    public async Task SaveProcess_NewGetsGuidAndUpdateKeepsId()
    {
        var saved = new { id = "", name = "API", command = "npm run dev", workingDirectory = @"C:\proj", env = new Dictionary<string, string> { ["A"] = "1" }, launchMode = "hidden", notes = (string?)null };

        var created = Data(await SendAsync("saveProcess", new { saved })).GetProperty("saved");
        var id = created.GetProperty("id").GetString()!;
        Assert.True(Guid.TryParse(id, out _));

        var updated = Data(await SendAsync("saveProcess", new { saved = saved with { id = id, name = "API 2" } })).GetProperty("saved");

        Assert.Equal(id, updated.GetProperty("id").GetString());
        var stored = Assert.Single(_h.Engine.Config.Saved);
        Assert.Equal("API 2", stored.Name);
        Assert.Equal("1", stored.Env["A"]);
    }

    [Fact]
    public async Task SaveProcess_RequiresNameAndCommand()
    {
        Error(await SendAsync("saveProcess", new { saved = new { id = "", name = "x", command = " " } }));
        Error(await SendAsync("saveProcess", new { saved = new { id = "", name = "", command = "dir" } }));
        Error(await SendAsync("saveProcess", new { }));
    }

    [Fact]
    public async Task DeleteSaved_RemovesAndUnknownIsError()
    {
        var created = Data(await SendAsync("saveProcess", new { saved = new { id = "", name = "x", command = "dir" } }));
        var id = created.GetProperty("saved").GetProperty("id").GetString();

        Data(await SendAsync("deleteSaved", new { id }));

        Assert.Empty(_h.Engine.Config.Saved);
        Assert.Empty(new ConfigStore(_h.ConfigPath).Load().Saved);
        Error(await SendAsync("deleteSaved", new { id }));
    }

    [Fact]
    public async Task StartSaved_UsesStarterAndReportsRunning()
    {
        var created = Data(await SendAsync("saveProcess", new { saved = new { id = "", name = "x", command = "npm run dev", workingDirectory = _h.Temp.Path, launchMode = "hidden" } }));
        var id = created.GetProperty("saved").GetProperty("id").GetString();

        var state = Data(await SendAsync("startSaved", new { id })).GetProperty("state");

        Assert.True(state.GetProperty("running").GetBoolean());
        Assert.Equal(5000, state.GetProperty("pids")[0].GetInt32());
        Assert.Contains("npm run dev", Assert.Single(_h.Starter.Launched).CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartSaved_MissingFolderIsReportedAndKeptAsLastError()
    {
        var created = Data(await SendAsync("saveProcess", new { saved = new { id = "", name = "x", command = "dir", workingDirectory = _h.Temp.Combine("sumiu") } }));
        var id = created.GetProperty("saved").GetProperty("id").GetString();

        Assert.Contains("pasta", Error(await SendAsync("startSaved", new { id })), StringComparison.Ordinal);
        Assert.NotNull(Assert.Single(_h.Engine.Sample().Saved).LastError);
        Assert.Empty(_h.Starter.Launched);
    }

    [Fact]
    public async Task StopSaved_KillsTrackedTree()
    {
        _h.AddProcess(5000, "cmd.exe", 1, createTime: 123);
        var created = Data(await SendAsync("saveProcess", new { saved = new { id = "", name = "x", command = "ping", launchMode = "hidden" } }));
        var id = created.GetProperty("saved").GetProperty("id").GetString();
        Data(await SendAsync("startSaved", new { id }));
        _h.Engine.Sample();

        var state = Data(await SendAsync("stopSaved", new { id })).GetProperty("state");

        Assert.False(state.GetProperty("running").GetBoolean());
        Assert.Contains(5000, _h.Killer.Killed);
    }

    [Fact]
    public async Task Ready_ReturnsSnapshotAndConfig()
    {
        _h.AddProcess(10, "a.exe", 100);

        var data = Data(await SendAsync("ready"));

        Assert.Equal(16_000, data.GetProperty("snapshot").GetProperty("machine").GetProperty("totalMemoryBytes").GetInt64());
        Assert.Equal(2000, data.GetProperty("config").GetProperty("settings").GetProperty("refreshIntervalMs").GetInt32());
    }

    [Fact]
    public async Task SetSettings_ValidatesAndPersists()
    {
        Assert.Contains("tema", Error(await SendAsync("setSettings", new { settings = new { refreshIntervalMs = 2000, minimizeToTray = true, theme = "rosa" } })), StringComparison.OrdinalIgnoreCase);

        var data = Data(await SendAsync("setSettings", new { settings = new { refreshIntervalMs = 3000, minimizeToTray = false, theme = "dark" } }));

        Assert.Equal("dark", data.GetProperty("settings").GetProperty("theme").GetString());
        Assert.Equal(3000, new ConfigStore(_h.ConfigPath).Load().Settings.RefreshIntervalMs);
    }

    [Fact]
    public async Task KillProcess_ProtectedIsRefused()
    {
        _h.AddProcess(4, "System", 1);
        _h.Engine.Sample();

        Assert.Contains("não pode ser encerrado", Error(await SendAsync("killProcess", new { pid = 4, tree = false })), StringComparison.Ordinal);
        Assert.Empty(_h.Killer.Killed);
    }

    [Fact]
    public async Task KillGroup_KillsInstancesFromLastSnapshot()
    {
        _h.AddProcess(10, "node.exe", 1);
        _h.AddProcess(11, "node.exe", 1);
        _h.Engine.Sample();

        var data = Data(await SendAsync("killGroup", new { key = "app:node.exe" }));

        Assert.Equal(2, data.GetProperty("killed").GetInt32());
    }

    [Fact]
    public async Task DraftFromProcess_FillsCommandAndFolderWithoutSaving()
    {
        _h.AddProcess(10, "node.exe", 1, commandLine: "node server.js", cwd: @"C:\proj");
        _h.Engine.Sample();

        var saved = Data(await SendAsync("draftFromProcess", new { pid = 10 })).GetProperty("saved");

        Assert.Equal("node server.js", saved.GetProperty("command").GetString());
        Assert.Equal(@"C:\proj", saved.GetProperty("workingDirectory").GetString());
        Assert.Equal(string.Empty, saved.GetProperty("id").GetString());
        Assert.Empty(_h.Engine.Config.Saved);
    }

    [Fact]
    public async Task DraftFromProcess_UnknownPidIsError()
    {
        Error(await SendAsync("draftFromProcess", new { pid = 4242 }));
    }

    [Fact]
    public async Task OpenFolder_InvalidPathIsError()
    {
        Error(await SendAsync("openFolder", new { path = @"Z:\nao\existe" }));
        Assert.Empty(_h.Starter.Started);
    }

    [Fact]
    public async Task GetIcons_UnknownPathsMapToNull()
    {
        var icons = Data(await SendAsync("getIcons", new { keys = new[] { @"z:\nao\existe.exe" } })).GetProperty("icons");

        Assert.Equal(JsonValueKind.Null, icons.GetProperty(@"z:\nao\existe.exe").ValueKind);
    }

    [Fact]
    public void BuildSnapshotMessage_UsesCamelCaseAndHidesInternals()
    {
        _h.AddProcess(10, "a.exe", 100, commandLine: "a", cwd: @"C:\");
        var json = Router.BuildSnapshotMessage(_h.Engine.Sample());

        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("snapshot", root.GetProperty("type").GetString());
        var process = root.GetProperty("payload").GetProperty("groups")[0].GetProperty("processes")[0];
        Assert.True(process.TryGetProperty("favoriteKey", out _));
        Assert.True(process.TryGetProperty("savedId", out var savedId));
        Assert.Equal(JsonValueKind.Null, savedId.ValueKind);
        Assert.False(process.TryGetProperty("createTime", out _));
    }
}
