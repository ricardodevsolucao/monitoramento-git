using System.ComponentModel;
using Monitoramento.Core.Actions;
using Monitoramento.Core.Models;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Actions;

public sealed class ProcessActionsTests : IDisposable
{
    private const int OwnPid = 999;
    private readonly TempDirectory _temp = new();
    private readonly FakeStarter _starter = new();
    private readonly FakeKiller _killer = new();

    private ProcessActions Actions => new(_starter, _killer, OwnPid);

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(0, "Idle")]
    [InlineData(4, "System")]
    [InlineData(OwnPid, "Monitoramento.exe")]
    [InlineData(500, "csrss.exe")]
    [InlineData(501, "LSASS.EXE")]
    [InlineData(502, "winlogon")]
    [InlineData(503, "Registry")]
    [InlineData(504, "dwm.exe")]
    public void Protection_RefusesCriticalProcesses(int pid, string name)
    {
        var error = ProcessProtection.CheckKill(pid, name, OwnPid);

        Assert.NotNull(error);
        Assert.Contains("não pode ser encerrado", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Protection_AllowsOrdinaryProcess()
    {
        Assert.Null(ProcessProtection.CheckKill(1234, "notepad.exe", OwnPid));
    }

    [Fact]
    public void KillProcess_RefusesProtectedAndDoesNotCallKiller()
    {
        _killer.Register(500, 1, "csrss.exe");
        var result = Actions.KillProcess(500, tree: false, [Proc.Info(500, "csrss.exe", 1)]);

        Assert.False(result.Ok);
        Assert.Empty(_killer.Killed);
    }

    [Fact]
    public void KillProcess_TreeCountsDescendants()
    {
        IReadOnlyList<ProcessInfo> snapshot =
        [
            Proc.Info(10, "cmd.exe", 1, createTime: 1),
            Proc.Info(11, "node.exe", 1, parentPid: 10, createTime: 2),
            Proc.Info(12, "node.exe", 1, parentPid: 11, createTime: 3),
        ];

        _killer.Register(snapshot);
        var result = Actions.KillProcess(10, tree: true, snapshot);

        Assert.True(result.Ok);
        Assert.Equal(3, result.Value);
        Assert.Equal([10, 11, 12], _killer.Killed.Order());
    }

    [Fact]
    public void KillProcess_RefusesTreeContainingProtectedProcess()
    {
        IReadOnlyList<ProcessInfo> snapshot =
        [
            Proc.Info(10, "x.exe", 1, createTime: 1),
            Proc.Info(11, "csrss.exe", 1, parentPid: 10, createTime: 2),
        ];

        _killer.Register(snapshot);
        Assert.False(Actions.KillProcess(10, tree: true, snapshot).Ok);
        Assert.Empty(_killer.Killed);
    }

    [Fact]
    public void KillProcess_ReportsAccessDeniedInPortuguese()
    {
        _killer.ThrowOnTerminate = new Win32Exception(5);

        _killer.Register(10, 1, "x.exe");
        var result = Actions.KillProcess(10, tree: false, [Proc.Info(10, "x.exe", 1)]);

        Assert.False(result.Ok);
        Assert.Contains("Não foi possível encerrar", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void KillGroup_KillsEveryInstance()
    {
        var group = new AppGroup
        {
            Key = "app:node.exe",
            Name = "Node",
            ExeName = "node.exe",
            Processes = [Proc.Info(1, "node.exe", 1), Proc.Info(2, "node.exe", 1)],
        };

        _killer.Register(group.Processes);
        var result = Actions.KillGroup("app:node.exe", [group]);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Value);
        Assert.Equal([1, 2], _killer.Killed.Order());
    }

    // S5
    [Fact]
    public void KillProcess_StartTimeFromPayloadMustMatchCurrentProcess()
    {
        var created = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
        _killer.Register(10, created.ToFileTimeUtc(), "node.exe");
        IReadOnlyList<ProcessInfo> snapshot = [Proc.Info(10, "node.exe", 1, createTime: created.ToFileTimeUtc())];

        var refused = Actions.KillProcess(10, tree: false, snapshot, startTime: created.AddSeconds(1).ToString("o"));
        var killed = Actions.KillProcess(10, tree: false, snapshot, startTime: created.ToString("o"));

        Assert.Contains("reutilizado", refused.Error, StringComparison.Ordinal);
        Assert.True(killed.Ok, killed.Error);
        Assert.Equal([10], _killer.Killed);
    }

    [Fact]
    public void KillProcess_InvalidStartTimeIsError()
    {
        _killer.Register(10, 1, "node.exe");

        var result = Actions.KillProcess(10, tree: false, [Proc.Info(10, "node.exe", 1)], startTime: "ontem");

        Assert.False(result.Ok);
        Assert.Empty(_killer.Killed);
    }

    [Fact]
    public void KillProcess_UnknownPidWithoutStartTimeIsRefused()
    {
        _killer.Register(10, 1, "node.exe");

        var result = Actions.KillProcess(10, tree: false, []);

        Assert.False(result.Ok);
        Assert.Empty(_killer.Killed);
    }

    [Fact]
    public void KillProcess_TreeSkipsDescendantWhosePidWasReused()
    {
        IReadOnlyList<ProcessInfo> snapshot =
        [
            Proc.Info(10, "cmd.exe", 1, createTime: 1),
            Proc.Info(11, "node.exe", 1, parentPid: 10, createTime: 2),
        ];
        _killer.Register(10, 1, "cmd.exe");
        _killer.Register(11, 50, "outro.exe"); // 11 morreu e o pid foi reaproveitado

        var result = Actions.KillProcess(10, tree: true, snapshot);

        Assert.True(result.Ok, result.Error);
        Assert.Equal([10], _killer.Killed);
    }

    [Fact]
    public void KillGroup_UsesCurrentNameForProtection()
    {
        var group = new AppGroup { Key = "app:x.exe", Name = "X", ExeName = "x.exe", Processes = [Proc.Info(1, "x.exe", 1), Proc.Info(2, "x.exe", 1)] };
        _killer.Register(1, 1, "x.exe");
        _killer.Register(2, 1, "winlogon.exe");

        Assert.False(Actions.KillGroup("app:x.exe", [group]).Ok);
        Assert.Empty(_killer.Killed);
    }

    [Fact]
    public void KillGroup_SkipsReusedPids()
    {
        var group = new AppGroup { Key = "app:x.exe", Name = "X", ExeName = "x.exe", Processes = [Proc.Info(1, "x.exe", 1), Proc.Info(2, "x.exe", 1)] };
        _killer.Register(1, 1, "x.exe");
        _killer.Register(2, 77, "x.exe");

        var result = Actions.KillGroup("app:x.exe", [group]);

        Assert.True(result.Ok);
        Assert.Equal(1, result.Value);
        Assert.Equal([1], _killer.Killed);
    }


    [Fact]
    public void KillGroup_UnknownKeyIsError()
    {
        Assert.False(Actions.KillGroup("app:nada.exe", []).Ok);
    }

    [Fact]
    public void OpenFolder_RejectsMissingOrRelativePath()
    {
        Assert.False(Actions.OpenFolder(_temp.Combine("nao-existe")).Ok);
        Assert.False(Actions.OpenFolder("relativa").Ok);
        Assert.False(Actions.OpenFolder(null).Ok);
        Assert.Empty(_starter.Started);
    }

    [Fact]
    public void OpenFolder_StartsExplorerWithPathAsArgument()
    {
        var result = Actions.OpenFolder(_temp.Path);

        Assert.True(result.Ok);
        var psi = Assert.Single(_starter.Started);
        Assert.EndsWith("explorer.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([_temp.Path], psi.ArgumentList);
        Assert.Equal(string.Empty, psi.Arguments);
    }

    [Fact]
    public void OpenTerminal_UsesWindowsTerminalWithDirectoryArgument()
    {
        var result = Actions.OpenTerminal(_temp.Path);

        Assert.True(result.Ok);
        var psi = Assert.Single(_starter.Started);
        Assert.Equal("wt.exe", psi.FileName);
        Assert.Equal(["-d", _temp.Path], psi.ArgumentList);
    }

    [Fact]
    public void OpenTerminal_FallsBackToCmdWhenWindowsTerminalIsMissing()
    {
        _starter.OnStart = psi => psi.FileName == "wt.exe"
            ? throw new Win32Exception(2)
            : new StartedProcess(1, 1);

        var result = Actions.OpenTerminal(_temp.Path);

        Assert.True(result.Ok);
        Assert.Equal(2, _starter.Started.Count);
        var fallback = _starter.Started[1];
        Assert.EndsWith("cmd.exe", fallback.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(_temp.Path, fallback.WorkingDirectory);
    }
}
