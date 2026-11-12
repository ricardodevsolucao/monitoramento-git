using System.Text.Json;
using Monitoramento.Core.Bridge;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Bridge;

/// <summary>Achados das revisões de segurança e qualidade que passam pela ponte (S3–S6, Q1, Q2, Q5, Q6).</summary>
public sealed class BridgeHardeningTests : IDisposable
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
        Assert.False(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        return reply.GetProperty("error").GetString()!;
    }

    private static JsonElement Data(JsonElement reply)
    {
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        return reply.GetProperty("data");
    }

    private async Task<string> CreateSavedAsync(string command = "npm run dev", string? workingDirectory = null, string launchMode = "hidden")
    {
        var created = Data(await SendAsync("saveProcess", new { saved = new { id = "", name = "x", command, workingDirectory, launchMode } }));
        return created.GetProperty("saved").GetProperty("id").GetString()!;
    }

    // S6
    [Fact]
    public async Task OversizedMessage_IsRefusedInPortuguese()
    {
        var huge = JsonSerializer.Serialize(new { id = "r1", type = "toggleFavorite", payload = new { key = "app:" + new string('a', 1_100_000) } });

        var reply = JsonDocument.Parse(await Router.HandleMessageAsync(huge)).RootElement;

        Assert.Contains("1 MB", Error(reply), StringComparison.Ordinal);
    }

    // S4
    [Theory]
    [InlineData("cmd /c echo oi & calc.exe", true)]
    [InlineData("node app.js > saida.txt", true)]
    [InlineData("node app.js ^ x", true)]
    [InlineData("echo %PATH%", true)]
    [InlineData("node \"a&b|c.js\" --porta 3000", false)]
    [InlineData("node server.js", false)]
    public async Task DraftFromProcess_WarnsAboutShellMetacharactersOutsideQuotes(string commandLine, bool warns)
    {
        _h.AddProcess(10, "node.exe", 1, commandLine: commandLine, cwd: @"C:\proj");
        _h.Engine.Sample();

        var data = Data(await SendAsync("draftFromProcess", new { pid = 10 }));

        var warning = data.GetProperty("warning");
        if (warns)
        {
            Assert.Equal(JsonValueKind.String, warning.ValueKind);
            Assert.Contains("aspas", warning.GetString(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, warning.ValueKind);
        }
    }

    // S5
    [Fact]
    public async Task KillProcess_WithDivergentStartTimeIsRefused()
    {
        _h.AddProcess(10, "node.exe", 1, createTime: DateTime.UtcNow.ToFileTimeUtc());
        _h.Engine.Sample();
        var stale = DateTime.UtcNow.AddHours(-1).ToString("o");

        var error = Error(await SendAsync("killProcess", new { pid = 10, tree = false, startTime = stale }));

        Assert.Contains("reutilizado", error, StringComparison.Ordinal);
        Assert.Empty(_h.Killer.Killed);
    }

    // S5: o processo foi trocado depois do último snapshot (mesmo pid, outro CreateTime)
    [Fact]
    public async Task KillProcess_PidReusedAfterSnapshotIsRefused()
    {
        _h.AddProcess(10, "node.exe", 1, createTime: 100);
        _h.Engine.Sample();
        _h.Killer.Replace(10, createTime: 200, name: "node.exe");

        var error = Error(await SendAsync("killProcess", new { pid = 10, tree = false }));

        Assert.Contains("reutilizado", error, StringComparison.Ordinal);
        Assert.Empty(_h.Killer.Killed);
    }

    // S5: proteção usa o nome atual do processo
    [Fact]
    public async Task KillProcess_ChecksProtectionWithCurrentName()
    {
        _h.AddProcess(10, "node.exe", 1, createTime: 100);
        _h.Engine.Sample();
        _h.Killer.Replace(10, createTime: 100, name: "lsass.exe");

        var error = Error(await SendAsync("killProcess", new { pid = 10, tree = false }));

        Assert.Contains("não pode ser encerrado", error, StringComparison.Ordinal);
        Assert.Empty(_h.Killer.Killed);
    }

    // S3
    [Fact]
    public async Task StopSaved_DoesNotKillReusedLaunchPid()
    {
        var id = await CreateSavedAsync(command: "ping", workingDirectory: null);
        Data(await SendAsync("startSaved", new { id })); // FakeStarter: pid 5000, CreateTime 123
        _h.AddProcess(5000, "chrome.exe", 1, createTime: 999); // o pid foi reaproveitado por outro processo
        _h.Engine.Sample();

        await SendAsync("stopSaved", new { id });

        Assert.Empty(_h.Killer.Killed);
    }

    // S3
    [Fact]
    public async Task StopSaved_KillsOnlyTrackedTreeWithSameIdentity()
    {
        _h.AddProcess(5000, "cmd.exe", 1, createTime: 123);
        _h.AddProcess(5001, "node.exe", 1, parentPid: 5000, createTime: 124);
        var id = await CreateSavedAsync(command: "ping");
        Data(await SendAsync("startSaved", new { id }));
        _h.Engine.Sample();

        Data(await SendAsync("stopSaved", new { id }));

        Assert.Equal([5000, 5001], _h.Killer.Killed.Order());
    }

    // Q1
    [Fact]
    public async Task Ready_ExposesConfigLoadError()
    {
        File.WriteAllText(_h.ConfigPath, "{ corrompido");

        var data = Data(await SendAsync("ready"));

        Assert.Equal(JsonValueKind.String, data.GetProperty("configLoadError").ValueKind);
    }

    [Fact]
    public async Task Ready_ConfigLoadErrorIsNullWhenFine()
    {
        var data = Data(await SendAsync("ready"));

        Assert.Equal(JsonValueKind.Null, data.GetProperty("configLoadError").ValueKind);
    }

    // Q1
    [Fact]
    public async Task LockedConfig_BlocksMutationsAndNeverOverwritesFile()
    {
        const string original = """{ "favorites": [ "app:code.exe" ] }""";
        File.WriteAllText(_h.ConfigPath, original);
        string? loadError;
        using (new FileStream(_h.ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            loadError = _h.Engine.ConfigLoadError;
            var error = Error(await SendAsync("toggleFavorite", new { key = "app:x.exe" }));
            Assert.Contains("configuração", error, StringComparison.OrdinalIgnoreCase);
        }

        Assert.NotNull(loadError);
        Assert.Equal(original, File.ReadAllText(_h.ConfigPath));

        // arquivo liberado: a próxima mutação relê e preserva o que já existia
        var favorites = Data(await SendAsync("toggleFavorite", new { key = "app:x.exe" })).GetProperty("favorites");
        Assert.Equal(["app:code.exe", "app:x.exe"], favorites.EnumerateArray().Select(e => e.GetString()));
        Assert.Null(_h.Engine.ConfigLoadError);
    }

    // Q2
    [Fact]
    public async Task StartSaved_TwiceDoesNotLaunchTwice()
    {
        var id = await CreateSavedAsync();

        Data(await SendAsync("startSaved", new { id }));
        var error = Error(await SendAsync("startSaved", new { id }));

        Assert.Contains("já está em execução", error, StringComparison.Ordinal);
        Assert.Single(_h.Starter.Launched);
    }

    // Q2
    [Fact]
    public async Task StartSaved_ParallelRequestsLaunchOnce()
    {
        var id = await CreateSavedAsync();
        _h.Starter.Delay = TimeSpan.FromMilliseconds(100);

        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => SendAsync("startSaved", new { id }, "p" + i)));

        Assert.Single(replies, r => r.GetProperty("ok").GetBoolean());
        Assert.Single(_h.Starter.Launched);
    }

    // Q5
    [Fact]
    public async Task SaveProcess_DuplicateEnvKeysLastOneWins()
    {
        var env = new Dictionary<string, string> { ["PORT"] = "1", ["port"] = "2", [" PORT "] = "3" };

        var saved = Data(await SendAsync("saveProcess", new { saved = new { id = "", name = "x", command = "dir", env } })).GetProperty("saved");

        var stored = Assert.Single(_h.Engine.Config.Saved);
        Assert.Equal("3", Assert.Single(stored.Env).Value);
        Assert.Single(saved.GetProperty("env").EnumerateObject());
    }

    // Q5
    [Fact]
    public async Task UnexpectedException_ReturnsGenericPortugueseMessage()
    {
        var id = await CreateSavedAsync();
        _h.Starter.OnLaunch = _ => throw new NotImplementedException("boom in english");

        var error = Error(await SendAsync("startSaved", new { id }));

        Assert.DoesNotContain("boom", error, StringComparison.Ordinal);
        Assert.Contains("Falha", error, StringComparison.Ordinal);
    }

    // Q6
    [Fact]
    public async Task DeleteSaved_ConcurrentDeletesSucceedOnlyOnce()
    {
        for (var round = 0; round < 20; round++)
        {
            var id = await CreateSavedAsync();

            var replies = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => SendAsync("deleteSaved", new { id }, "d" + i)));

            Assert.Single(replies, r => r.GetProperty("ok").GetBoolean());
        }
    }
}
