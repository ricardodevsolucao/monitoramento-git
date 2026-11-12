using System.Diagnostics;
using System.Text.Json;
using Monitoramento.Core.Actions;
using Monitoramento.Core.Bridge;
using Monitoramento.Core.Config;
using Monitoramento.Core.Launcher;
using Monitoramento.Core.Models;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Engine;

/// <summary>Q3 (dono do processo), Q7 (falha transitória da coleta), Q12 (I/O fora do lock) e concorrência básica.</summary>
public sealed class EngineRobustnessTests : IDisposable
{
    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    private BridgeRouter Router => new(_h.Engine);

    private async Task<JsonElement> SendAsync(string type, object payload, string id = "r1")
    {
        var reply = await Router.HandleMessageAsync(JsonSerializer.Serialize(new { id, type, payload }));
        return JsonDocument.Parse(reply).RootElement.Clone();
    }

    private void WriteConfig(string json) => File.WriteAllText(_h.ConfigPath, json);

    // Q7
    [Fact]
    public void Sample_ReusesPreviousCycleWhenCollectionFails()
    {
        _h.AddProcess(10, "a.exe", 100);
        var first = _h.Engine.Sample();

        _h.Source.Fail = true;
        var second = _h.Engine.Sample();

        Assert.Same(first, second);
        Assert.Same(first, _h.Engine.LastSnapshot);
    }

    // Q7
    [Fact]
    public void Sample_FailureBeforeFirstCycleReturnsEmptySnapshot()
    {
        _h.Source.Fail = true;

        var snapshot = _h.Engine.Sample();

        Assert.Empty(snapshot.Groups);
    }

    // Q7
    [Fact]
    public async Task Sample_FailedCollectionDoesNotDropLaunchTracking()
    {
        WriteConfig("""{ "saved": [ { "id": "s1", "name": "x", "command": "ping", "launchMode": "hidden" } ] }""");
        _h.AddProcess(5000, "cmd.exe", 1, createTime: 123);
        Assert.True((await SendAsync("startSaved", new { id = "s1" })).GetProperty("ok").GetBoolean());
        _h.Engine.Sample();

        _h.Time.Advance(TimeSpan.FromSeconds(30));
        _h.Source.Fail = true;
        _h.Engine.Sample();
        _h.Source.Fail = false;
        var state = Assert.Single(_h.Engine.Sample().Saved);

        Assert.True(state.Running);
        Assert.Equal([5000], state.Pids);
    }

    // Q7
    [Fact]
    public void Launcher_KeepsLaunchesInCycleWithoutProcesses()
    {
        var launcher = new SavedProcessLauncher(_h.Starter, _h.Killer, _h.Time, EngineHarness.OwnPid);
        var saved = new SavedProcess { Id = "s1", Name = "x", Command = "ping" };
        Assert.True(launcher.Start(saved, alreadyRunning: false).Ok);
        _h.Time.Advance(TimeSpan.FromSeconds(30));

        launcher.ComputeStates([saved], [], _h.Git);
        var (states, _) = launcher.ComputeStates([saved], [Proc.Info(5000, "cmd.exe", 1, createTime: 123)], _h.Git);

        Assert.True(Assert.Single(states).Running);
    }

    // Q3
    [Fact]
    public void Sample_ProcessBelongsToSingleSavedPreferringLongestCommand()
    {
        WriteConfig("""
            { "saved": [
              { "id": "curto", "name": "dev", "command": "npm run dev", "workingDirectory": "C:\\proj" },
              { "id": "longo", "name": "dev host", "command": "npm run dev --host", "workingDirectory": "C:\\proj" },
              { "id": "api", "name": "api", "command": "npm run dev:api", "workingDirectory": "C:\\proj" }
            ] }
            """);
        _h.AddProcess(10, "cmd.exe", 1, commandLine: "cmd.exe /c npm run dev --host", cwd: @"C:\proj", createTime: 5);

        var states = _h.Engine.Sample().Saved.ToDictionary(s => s.Id);

        Assert.False(states["curto"].Running);
        Assert.True(states["longo"].Running);
        Assert.False(states["api"].Running);
    }

    // Q3
    [Fact]
    public async Task Sample_ProcessBelongsToSavedWithLaunchRecord()
    {
        var proj = _h.Temp.Path;
        WriteConfig(JsonSerializer.Serialize(new
        {
            saved = new[]
            {
                new { id = "longo", name = "dev host", command = "npm run dev --host", workingDirectory = proj },
                new { id = "iniciado", name = "dev", command = "npm run dev", workingDirectory = proj },
            },
        }));
        _h.Starter.OnLaunch = _ => new StartedProcess(10, 5);
        var started = await SendAsync("startSaved", new { id = "iniciado" });
        Assert.True(started.GetProperty("ok").GetBoolean(), started.ToString());
        _h.AddProcess(10, "cmd.exe", 1, commandLine: "cmd.exe /c npm run dev --host", cwd: proj, createTime: 5);

        var states = _h.Engine.Sample().Saved.ToDictionary(s => s.Id);

        Assert.True(states["iniciado"].Running);
        Assert.False(states["longo"].Running);
    }

    // Q12
    [Fact]
    public async Task ComputeStates_ResolvesGitOutsideLauncherLock()
    {
        WriteConfig("""
            { "saved": [
              { "id": "lento", "name": "a", "command": "a", "workingDirectory": "C:\\lento" },
              { "id": "outro", "name": "b", "command": "b" }
            ] }
            """);
        using var resolving = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _h.Git.OnResolve = dir =>
        {
            if (dir == @"C:\lento")
            {
                resolving.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
        };

        var sampling = Task.Run(() => _h.Engine.Sample());
        Assert.True(resolving.Wait(TimeSpan.FromSeconds(5)));
        var watch = Stopwatch.StartNew();
        var start = SendAsync("startSaved", new { id = "outro" });
        var finished = await Task.WhenAny(start, Task.Delay(2000)) == start;
        release.Set();
        await sampling;

        Assert.True(finished, $"startSaved ficou bloqueado pelo git ({watch.ElapsedMilliseconds} ms)");
    }

    // Concorrência: Sample em laço + mensagens em paralelo
    [Fact]
    public async Task Concurrency_SampleLoopWithParallelMessagesStaysConsistent()
    {
        for (var i = 0; i < 20; i++)
        {
            _h.AddProcess(100 + i, $"app{i % 4}.exe", 1000 + i, commandLine: $"app{i % 4} --n {i}", cwd: @"C:\proj", createTime: 10 + i);
        }

        _h.Engine.Sample();
        using var stop = new CancellationTokenSource();
        var samples = 0;
        var loop = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                _h.Engine.Sample();
                Interlocked.Increment(ref samples);
            }
        });

        var favoriteTasks = Enumerable.Range(0, 30).Select(i => SendAsync("toggleFavorite", new { key = $"app:x{i}.exe" }, "f" + i));
        var saveTasks = Enumerable.Range(0, 10).Select(i =>
            SendAsync("saveProcess", new { saved = new { id = "", name = "s" + i, command = $"cmd{i} run", workingDirectory = (string?)null, launchMode = "hidden" } }, "s" + i));
        var replies = await Task.WhenAll(favoriteTasks.Concat(saveTasks));

        var ids = _h.Engine.Config.Saved.Select(s => s.Id).ToList();
        var starts = await Task.WhenAll(ids.SelectMany(id => new[] { SendAsync("startSaved", new { id }, "a" + id), SendAsync("startSaved", new { id }, "b" + id) }));
        await stop.CancelAsync();
        await loop;

        Assert.All(replies, r => Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString()));
        Assert.True(samples > 0);
        var config = _h.Engine.Config;
        Assert.Equal(30, config.Favorites.Count);
        Assert.Equal(30, config.Favorites.Distinct().Count());
        Assert.Equal(10, config.Saved.Count);
        Assert.Equal(10, starts.Count(r => r.GetProperty("ok").GetBoolean())); // um início por processo salvo
        Assert.Equal(10, _h.Starter.Launched.Count);

        var stored = new ConfigStore(_h.ConfigPath).Load();
        Assert.Equal(config.Favorites.Order(), stored.Favorites.Order());
        Assert.Equal(10, stored.Saved.Count);
        _h.Engine.Sample();
    }
}
