using System.Diagnostics;
using Monitoramento.Core.Models;
using Monitoramento.Core.Terminal;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Terminal;

/// <summary>Correções da revisão de segurança dos terminais (M1, B5, B6) e o terminal de comando montado pelo app.</summary>
public sealed class TerminalFixesTests : IDisposable
{
    private static readonly TerminalLaunch Shell = new(@"C:\pwsh\pwsh.exe", "pwsh -NoLogo", new Dictionary<string, string>());
    private static readonly TerminalLaunch Cmd = new(@"C:\Windows\System32\cmd.exe", "cmd /s /k \"npm run dev\"", new Dictionary<string, string> { ["PORT"] = "3000" });

    private readonly FakePtyFactory _pty = new();
    private readonly TerminalManager _manager;

    public TerminalFixesTests()
    {
        _manager = new TerminalManager(_pty, () => Shell);
    }

    public void Dispose() => _manager.Dispose();

    private static string? NoProject(string folder) => null;

    [Fact]
    public void Restart_OfSavedTerminal_IsRefusedByManager()
    {
        var saved = new SavedProcess { Id = "s1", Name = "API", Command = "npm run dev" };
        var id = _manager.StartSaved(saved, Cmd, @"C:\src\api").Value!.Terminal.Id;

        var result = _manager.Restart(id);

        Assert.Equal(TerminalManager.SavedRestartError, result.Error);
        Assert.Single(_pty.Sessions);
    }

    [Fact]
    public void Unlink_DetachesTheSaved_AndRestartSaysItWasDeleted()
    {
        var saved = new SavedProcess { Id = "s1", Name = "API", Command = "npm run dev" };
        var id = _manager.StartSaved(saved, Cmd, @"C:\src\api").Value!.Terminal.Id;
        _pty.Last.Exit(0);

        _manager.Unlink("s1");

        Assert.Null(_manager.TerminalIdForSaved("s1"));
        Assert.Null(Assert.Single(_manager.List(NoProject)).SavedId);
        Assert.Equal(TerminalManager.SavedDeletedError, _manager.Restart(id).Error);
        Assert.Equal("O processo salvo foi excluído; feche este terminal.", TerminalManager.SavedDeletedError);
    }

    [Fact]
    public void OpenCommand_RunsTheGivenLaunch_WithoutSavedLink_AndCanRestart()
    {
        var info = _manager.OpenCommand("API ⎇ feat", @"C:\wt\app-feat\api", Cmd, 90, 20).Value!;

        Assert.Equal("API ⎇ feat", info.Title);
        Assert.Null(info.SavedId);
        Assert.Equal(@"C:\wt\app-feat\api", _pty.Last.Info.WorkingDirectory);
        Assert.Equal("3000", _pty.Last.Info.Environment["PORT"]);
        Assert.True(_manager.Restart(info.Id).Ok);
        Assert.Equal(Cmd.CommandLine, _pty.Last.Info.CommandLine);
    }

    [Fact]
    public void RunningCount_CountsTreesWithLiveDescendants_AndStopKillsThem()
    {
        var id = _manager.Open(@"C:\src\app", null, null).Value!.Id;
        var session = _pty.Last;
        session.Exit(0);
        session.HasLiveProcesses = true; // o shell saiu, um filho continua no Job Object

        Assert.Equal(1, _manager.RunningCount);

        _manager.StopAndWait(id, out _);

        Assert.True(session.Killed);
        Assert.Equal(0, _manager.RunningCount);
    }

    [Fact]
    public void CloseAll_UsesOneGlobalDeadline()
    {
        for (var i = 0; i < 4; i++)
        {
            _manager.Open($@"C:\src\t{i}", null, null);
            _pty.Last.IgnoreKill = true; // nunca termina
        }

        var watch = Stopwatch.StartNew();
        _manager.CloseAll();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"levou {watch.Elapsed}");
        Assert.All(_pty.Sessions, s => Assert.True(s.Disposed));
    }
}
