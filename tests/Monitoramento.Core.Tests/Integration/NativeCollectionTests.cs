using System.Diagnostics;
using Monitoramento.Core.Collection;
using Monitoramento.Core.Icons;
using Monitoramento.Core.Interop;
using Monitoramento.Core.Launcher;
using Monitoramento.Core.Tests.TestSupport;
using Xunit.Abstractions;

namespace Monitoramento.Core.Tests.Integration;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeCollection
{
    public const string Name = "Win32 real";
}

[Collection(NativeCollection.Name)]
public sealed class NativeCollectionTests(ITestOutputHelper output)
{
    [Fact]
    public void ProcessSource_ListsOwnProcessWithParentAndMemory()
    {
        var all = new NtProcessSource().ReadAll()!;

        var self = Assert.Single(all, p => p.Pid == Environment.ProcessId);
        Assert.True(self.ParentPid > 0);
        Assert.True(self.PrivateBytes > 0);
        Assert.True(self.CreateTime > 0);
        Assert.True(self.CpuTime > 0);
        Assert.EndsWith(".exe", self.Name, StringComparison.OrdinalIgnoreCase);
        Assert.True(all.Count > 20);
    }

    [Fact]
    public void Reader_ReadsOwnCommandLineAndWorkingDirectory()
    {
        var details = new NativeProcessReader().Read(Environment.ProcessId);

        Assert.False(details.AccessDenied);
        Assert.NotNull(details.CommandLine);
        Assert.NotNull(details.WorkingDirectory);
        Assert.True(PathText.AreEqual(Environment.CurrentDirectory, details.WorkingDirectory));
        Assert.Equal(Environment.ProcessPath, details.ExePath, ignoreCase: true);
    }

    [Fact]
    public void Reader_ReadsChildStartedInKnownFolder()
    {
        using var temp = new TempDirectory();
        using var child = StartPing(Path.Combine(Environment.SystemDirectory, "cmd.exe"), temp.Path);

        try
        {
            var details = new NativeProcessReader().Read(child.Id);

            Assert.True(PathText.AreEqual(temp.Path, details.WorkingDirectory), details.WorkingDirectory);
            Assert.Contains("ping", details.CommandLine, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void Reader_ReadsWow64ChildWorkingDirectory()
    {
        var cmd32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "cmd.exe");
        Assert.True(File.Exists(cmd32), "SysWOW64\\cmd.exe ausente");
        using var temp = new TempDirectory();
        using var child = StartPing(cmd32, temp.Path);

        try
        {
            var details = new NativeProcessReader().Read(child.Id);

            Assert.True(PathText.AreEqual(temp.Path, details.WorkingDirectory), details.WorkingDirectory);
            Assert.Contains("ping", details.CommandLine, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void Reader_SystemProcessIsDeniedWithoutThrowing()
    {
        var details = new NativeProcessReader().Read(4);

        Assert.True(details.AccessDenied);
        Assert.Null(details.WorkingDirectory);
    }

    [Fact]
    public void SystemInfo_ReadsMemoryAndCores()
    {
        var info = new NativeSystemInfoSource();

        var memory = info.ReadMemory();
        var times = info.ReadCpuTimes();

        Assert.True(memory.TotalBytes > 0);
        Assert.InRange(memory.AvailableBytes, 1, memory.TotalBytes);
        Assert.True(times.Kernel >= times.Idle);
        Assert.Equal(Environment.ProcessorCount, info.LogicalCores);
    }

    [Fact]
    public void IconProvider_ReturnsPngForExecutable()
    {
        var icons = new IconProvider();
        var path = Path.Combine(Environment.SystemDirectory, "notepad.exe");

        var png = icons.GetPng(path);

        Assert.NotNull(png);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        Assert.StartsWith("data:image/png;base64,", icons.GetDataUrl(path.ToLowerInvariant()), StringComparison.Ordinal);
        Assert.Null(icons.GetDataUrl(@"c:\nao\existe.exe"));
    }

    [Fact]
    public void Engine_SamplesRealMachineWithinBudget()
    {
        using var temp = new TempDirectory();
        using var engine = new MonitorEngine(temp.Combine("config.json"));

        for (var i = 0; i < 3; i++)
        {
            engine.Sample();
        }

        var timings = new List<double>();
        Models.Snapshot? snapshot = null;
        for (var i = 0; i < 10; i++)
        {
            var sw = Stopwatch.StartNew();
            snapshot = engine.Sample();
            timings.Add(sw.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        var median = timings[timings.Count / 2];
        output.WriteLine($"processos={snapshot!.Machine.ProcessCount} grupos={snapshot.Groups.Count} mediana={median:F1} ms min={timings[0]:F1} max={timings[^1]:F1}");

        var self = snapshot.Groups.SelectMany(g => g.Processes).Single(p => p.Pid == Environment.ProcessId);
        Assert.NotNull(self.CommandLine);
        Assert.NotNull(self.WorkingDirectory);
        Assert.InRange(snapshot.Machine.MemoryPercent, 1, 100);
        Assert.True(median < 150, $"Sample() mediana {median:F1} ms");
    }

    [Fact]
    public async Task Launcher_StartsHiddenProcessTracksAndStopsIt()
    {
        using var temp = new TempDirectory();
        using var engine = new MonitorEngine(temp.Combine("config.json"));
        var router = new Monitoramento.Core.Bridge.BridgeRouter(engine);
        var work = Directory.CreateDirectory(temp.Combine("work")).FullName;

        var saveReply = await router.HandleMessageAsync(Json(new
        {
            id = "1",
            type = "saveProcess",
            payload = new { saved = new { id = "", name = "ping", command = "ping -n 60 127.0.0.1 > nul", workingDirectory = work, launchMode = "hidden" } },
        }));
        var id = System.Text.Json.JsonDocument.Parse(saveReply).RootElement.GetProperty("data").GetProperty("saved").GetProperty("id").GetString();

        var startReply = await router.HandleMessageAsync(Json(new { id = "2", type = "startSaved", payload = new { id } }));
        Assert.Contains("\"ok\":true", startReply, StringComparison.Ordinal);

        var running = await WaitForAsync(() => engine.Sample().Saved.Single(), s => s.Running && s.Pids.Count >= 2);
        output.WriteLine($"pids={string.Join(',', running.Pids)}");
        Assert.True(running.Running);

        var stopReply = await router.HandleMessageAsync(Json(new { id = "3", type = "stopSaved", payload = new { id } }));
        Assert.Contains("\"ok\":true", stopReply, StringComparison.Ordinal);

        var stopped = await WaitForAsync(() => engine.Sample().Saved.Single(), s => !s.Running);
        Assert.False(stopped.Running);
        foreach (var pid in running.Pids)
        {
            Assert.DoesNotContain(new NtProcessSource().ReadAll()!, p => p.Pid == pid && p.Name.Equals("PING.EXE", StringComparison.OrdinalIgnoreCase));
        }
    }

    // S3: o processo iniciado é o próprio cmd rastreado (sem cmd externo), com pasta e variáveis aplicadas
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Starter_LaunchedRootIsTrackedCmdWithEnvAndFolder(bool newConsole)
    {
        using var temp = new TempDirectory();
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var spec = new Monitoramento.Core.Actions.LaunchSpec(
            cmd,
            $"\"{cmd}\" /s /{(newConsole ? 'k' : 'c')} \"echo %MONITORAMENTO_TESTE%> saida.txt & ping -n 30 127.0.0.1 > nul\"",
            temp.Path,
            new Dictionary<string, string> { ["MONITORAMENTO_TESTE"] = "ola" },
            newConsole,
            "Teste Monitoramento");

        var started = new Monitoramento.Core.Actions.SystemProcessStarter().Launch(spec);
        var killer = new Monitoramento.Core.Actions.SystemProcessKiller();
        try
        {
            var root = Assert.Single(new NtProcessSource().ReadAll()!, p => p.Pid == started.Pid);
            Assert.Equal("cmd.exe", root.Name, ignoreCase: true);
            Assert.Equal(root.CreateTime, started.CreateTime);

            var output = Path.Combine(temp.Path, "saida.txt");
            for (var i = 0; i < 50 && !File.Exists(output); i++)
            {
                Thread.Sleep(100);
            }

            Assert.Equal("ola", File.ReadAllText(output).Trim());
        }
        finally
        {
            foreach (var p in new NtProcessSource().ReadAll()!.Where(p => p.ParentPid == started.Pid || p.Pid == started.Pid))
            {
                using var handle = killer.Open(p.Pid);
                handle?.Terminate();
            }
        }
    }

    // S5: o encerramento confere a identidade pelo mesmo handle
    [Fact]
    public void Killer_VerifiesIdentityBeforeTerminating()
    {
        using var temp = new TempDirectory();
        using var child = StartPing(Path.Combine(Environment.SystemDirectory, "cmd.exe"), temp.Path);
        var raw = Assert.Single(new NtProcessSource().ReadAll()!, p => p.Pid == child.Id);
        var killer = new Monitoramento.Core.Actions.SystemProcessKiller();

        try
        {
            var refused = Monitoramento.Core.Actions.ProcessTerminator.KillOne(killer, child.Id, raw.CreateTime + 1, snapshotName: null, ownPid: Environment.ProcessId);
            Assert.Contains("reutilizado", refused, StringComparison.Ordinal);
            Assert.False(child.HasExited);

            using (var handle = killer.Open(child.Id))
            {
                Assert.NotNull(handle);
                Assert.Equal(raw.CreateTime, handle.CreateTime);
                Assert.Equal("cmd.exe", handle.Name, ignoreCase: true);
            }

            Assert.Null(Monitoramento.Core.Actions.ProcessTerminator.KillOne(killer, child.Id, raw.CreateTime, snapshotName: "cmd.exe", ownPid: Environment.ProcessId));
            Assert.True(child.WaitForExit(5000));
            Assert.Null(killer.Open(child.Id));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
    }

    private static string Json(object value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static async Task<T> WaitForAsync<T>(Func<T> read, Func<T, bool> done)
    {
        var value = read();
        for (var i = 0; i < 50 && !done(value); i++)
        {
            await Task.Delay(100);
            value = read();
        }

        return value;
    }

    private static Process StartPing(string cmdPath, string workingDirectory)
    {
        var psi = new ProcessStartInfo(cmdPath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("ping -n 30 127.0.0.1 > nul");
        var process = Process.Start(psi)!;

        // dá tempo ao processo de inicializar o PEB/parâmetros
        Thread.Sleep(300);
        return process;
    }
}
