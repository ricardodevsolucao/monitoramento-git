using Monitoramento.Core.Favorites;
using Monitoramento.Core.Models;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Engine;

public sealed class MonitorEngineTests : IDisposable
{
    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void Sample_FillsMachineInfo()
    {
        _h.AddProcess(10, "a.exe", 100);
        _h.AddProcess(11, "b.exe", 100);

        var machine = _h.Engine.Sample().Machine;

        Assert.Equal(16_000, machine.TotalMemoryBytes);
        Assert.Equal(12_000, machine.UsedMemoryBytes);
        Assert.Equal(75.0, machine.MemoryPercent, precision: 6);
        Assert.Equal(4, machine.LogicalCores);
        Assert.Equal(2, machine.ProcessCount);
        Assert.Single(machine.History);
    }

    [Fact]
    public void Sample_SkipsIdleProcessAndGroupsTheRest()
    {
        _h.AddProcess(0, "", 0);
        _h.AddProcess(10, "code.exe", 300, exePath: @"C:\VS\Code.exe");
        _h.AddProcess(11, "Code.exe", 200);

        var snapshot = _h.Engine.Sample();

        var group = Assert.Single(snapshot.Groups);
        Assert.Equal("app:code.exe", group.Key);
        Assert.Equal(500, group.MemoryBytes);
        Assert.Equal([500L], group.MemoryHistory);
    }

    [Fact]
    public void Sample_ComputesProcessCpuBetweenCycles()
    {
        _h.AddProcess(10, "a.exe", 1, cpu: 0);
        _h.Engine.Sample();

        _h.AddProcess(10, "a.exe", 1, cpu: 2_000_000);
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        var process = _h.Engine.Sample().Groups[0].Processes[0];

        Assert.Equal(5.0, process.CpuPercent, precision: 6);
    }

    [Fact]
    public void Sample_FillsProcessDetailsGitAndFavoriteKey()
    {
        var git = new GitInfo(@"C:\proj", "proj", "main", 0, null, null);
        _h.Git.ByDirectory[@"C:\proj"] = git;
        _h.AddProcess(10, "node.exe", 1, commandLine: "node server.js", cwd: @"C:\proj");

        var process = _h.Engine.Sample().Groups[0].Processes[0];

        Assert.Equal("node server.js", process.CommandLine);
        Assert.Equal(@"C:\proj", process.WorkingDirectory);
        Assert.False(process.AccessDenied);
        Assert.Equal(git, process.Git);
        Assert.Equal(FavoriteKeys.ForCommand("node.exe", "node server.js"), process.FavoriteKey);
        Assert.NotNull(process.StartTime);
    }

    [Fact]
    public void Sample_WorkspaceWithoutGit_ListsSubReposAndGroupsThemDistinct()
    {
        var app = new GitInfo(@"C:\ws\app", "app", "main", 0, null, null);
        var api = new GitInfo(@"C:\ws\api", "api", "dev", 2, null, null);
        _h.Git.ChildrenByDirectory[@"C:\ws"] = [api, app];
        _h.AddProcess(10, "node.exe", 2, commandLine: "node a", cwd: @"C:\ws");
        _h.AddProcess(11, "node.exe", 1, commandLine: "node b", cwd: @"C:\ws");

        var group = _h.Engine.Sample().Groups.Single();

        Assert.Equal([api, app], group.Processes[0].SubRepos);
        Assert.Null(group.Processes[0].Git);
        Assert.Equal([api, app], group.Repos);
    }

    [Fact]
    public void Sample_ProcessInsideRepo_HasNoSubRepos()
    {
        _h.Git.ByDirectory[@"C:\proj"] = new GitInfo(@"C:\proj", "proj", "main", 0, null, null);
        _h.Git.ChildrenByDirectory[@"C:\proj"] = [new GitInfo(@"C:\proj\vendor", "vendor", "main", 0, null, null)];
        _h.AddProcess(10, "node.exe", 1, cwd: @"C:\proj");

        Assert.Empty(_h.Engine.Sample().Groups[0].Processes[0].SubRepos);
    }

    [Fact]
    public void Sample_MarksGitProcesses()
    {
        _h.AddProcess(10, "git.exe", 1);

        Assert.True(_h.Engine.Sample().Groups[0].Processes[0].IsGitProcess);
    }

    [Fact]
    public void Sample_ReportsSavedProcessRunningByCorrespondence()
    {
        File.WriteAllText(_h.ConfigPath, """
            { "saved": [ { "id": "s1", "name": "dev", "command": "npm run dev", "workingDirectory": "C:\\proj", "launchMode": "hidden" } ] }
            """);
        _h.AddProcess(10, "cmd.exe", 100, commandLine: "cmd.exe /c npm run dev", cwd: @"C:\proj", createTime: 5);
        _h.AddProcess(11, "node.exe", 50, parentPid: 10, createTime: 6);

        var snapshot = _h.Engine.Sample();

        var state = Assert.Single(snapshot.Saved);
        Assert.True(state.Running);
        Assert.Equal([10, 11], state.Pids.Order());
        Assert.Equal(150, state.MemoryBytes);
        Assert.Equal("s1", snapshot.Groups.Single(g => g.Key == "app:cmd.exe").Processes[0].SavedId);
    }

    // Q10
    [Fact]
    public void Sample_ProcessWithoutCommandLineUsesGroupFavoriteKey()
    {
        _h.AddProcess(10, "svc.exe", 1);
        _h.AddProcess(11, "svc.exe", 1);
        _h.AddProcess(12, "outro.exe", 1);
        _h.Details.Details[10] = new Monitoramento.Core.Collection.ProcessDetails(null, null, null, AccessDenied: true);
        _h.Details.Details[11] = new Monitoramento.Core.Collection.ProcessDetails(null, "  ", null, AccessDenied: true);
        _h.Details.Details[12] = new Monitoramento.Core.Collection.ProcessDetails(null, null, null, AccessDenied: true);

        var processes = _h.Engine.Sample().Groups.SelectMany(g => g.Processes).ToDictionary(p => p.Pid);

        Assert.Equal("app:svc.exe", processes[10].FavoriteKey);
        Assert.Equal("app:svc.exe", processes[11].FavoriteKey);
        Assert.Equal("app:outro.exe", processes[12].FavoriteKey);
    }

    [Fact]
    public void Constructor_ExposesConfigAndLoadError()
    {
        File.WriteAllText(_h.ConfigPath, "corrompido");

        Assert.Equal(2000, _h.Engine.Config.Settings.RefreshIntervalMs);
        Assert.NotNull(_h.Engine.ConfigLoadError);
    }
}
