using Monitoramento.Core.Models;
using Monitoramento.Core.Terminal;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Terminal;

/// <summary>Gerenciador de terminais com pseudo-console falso: limites, validação de id, eventos e reinício.</summary>
public sealed class TerminalManagerTests : IDisposable
{
    private static readonly TerminalLaunch Shell = new(@"C:\pwsh\pwsh.exe", "\"C:\\pwsh\\pwsh.exe\" -NoLogo", new Dictionary<string, string>());

    private readonly FakePtyFactory _pty = new();
    private readonly TerminalManager _manager;

    public TerminalManagerTests()
    {
        _manager = new TerminalManager(_pty, () => Shell, new ManualTimeProvider(), bufferCapacity: 64);
    }

    public void Dispose() => _manager.Dispose();

    private static string? NoProject(string folder) => null;

    [Fact]
    public void Open_StartsDefaultShellInFolder_WithClampedSize()
    {
        var info = _manager.Open(@"C:\src\app", 5, 999).Value!;

        var started = _pty.Last.Info;
        Assert.Equal(@"C:\pwsh\pwsh.exe", started.ApplicationName);
        Assert.Equal("\"C:\\pwsh\\pwsh.exe\" -NoLogo", started.CommandLine);
        Assert.Equal(@"C:\src\app", started.WorkingDirectory);
        Assert.Equal((TerminalManager.MinCols, TerminalManager.MaxRows), (started.Cols, started.Rows));
        Assert.Equal("app", info.Title);
        Assert.True(info.Running);
        Assert.Equal(_pty.Last.Pid, info.Pid);
        Assert.Null(info.SavedId);
    }

    [Fact]
    public void Open_DefaultSize_WhenMissing()
    {
        var info = _manager.Open(@"C:\src\app", null, null).Value!;

        Assert.Equal((TerminalManager.DefaultCols, TerminalManager.DefaultRows), (info.Cols, info.Rows));
    }

    [Fact]
    public void Open_AtMostSixteenTerminals()
    {
        for (var i = 0; i < TerminalManager.MaxTerminals; i++)
        {
            Assert.True(_manager.Open($@"C:\src\t{i}", null, null).Ok);
        }

        var refused = _manager.Open(@"C:\src\extra", null, null);

        Assert.Equal(TerminalManager.LimitError, refused.Error);
        Assert.Equal("Limite de 16 terminais abertos. Feche um terminal para abrir outro.", refused.Error);
    }

    [Fact]
    public void Open_WithoutConPty_IsRefusedInPortuguese()
    {
        _pty.IsSupported = false;

        Assert.Equal(TerminalManager.UnsupportedError, _manager.Open(@"C:\src\app", null, null).Error);
    }

    [Fact]
    public void Open_StartFailure_ReportsWin32Code()
    {
        _pty.FailWith = 5;

        var result = _manager.Open(@"C:\src\app", null, null);

        Assert.Equal("Não foi possível iniciar o terminal (erro 5).", result.Error);
        Assert.Empty(_manager.List(NoProject));
    }

    [Fact]
    public void Input_GoesToSession_OnlyForExistingRunningTerminal()
    {
        var id = _manager.Open(@"C:\src\app", null, null).Value!.Id;

        Assert.True(_manager.Input(id, "dir\r").Ok);
        Assert.Equal(["dir\r"], _pty.Last.Written);

        Assert.Equal(TerminalManager.NotFoundError, _manager.Input("inexistente", "x").Error);
        _pty.Last.Exit(0);
        Assert.Equal(TerminalManager.NotRunningError, _manager.Input(id, "x").Error);
    }

    [Fact]
    public void Input_LargerThan64KB_IsRefused()
    {
        var id = _manager.Open(@"C:\src\app", null, null).Value!.Id;

        var result = _manager.Input(id, new string('é', 33 * 1024)); // 2 bytes cada em UTF-8: 66 KB

        Assert.Equal(TerminalManager.InputTooLargeError, result.Error);
        Assert.Empty(_pty.Last.Written);
    }

    [Theory]
    [InlineData(1, 1, 20, 5)]
    [InlineData(80, 24, 80, 24)]
    [InlineData(10_000, 10_000, 500, 200)]
    public void Resize_IsClamped(int cols, int rows, int expectedCols, int expectedRows)
    {
        var id = _manager.Open(@"C:\src\app", null, null).Value!.Id;

        Assert.True(_manager.Resize(id, cols, rows).Ok);

        Assert.Equal((expectedCols, expectedRows), _pty.Last.LastResize);
        Assert.Equal(TerminalManager.NotFoundError, _manager.Resize("x", 80, 24).Error);
    }

    [Fact]
    public void Output_BecomesEvents_MergedPerTerminal_AndAttachReturnsRing()
    {
        var id = _manager.Open(@"C:\src\app", null, null).Value!.Id;
        _pty.Last.Emit("ola ");
        _pty.Last.Emit("mundo");

        var events = _manager.TakeEvents();
        var attach = _manager.Attach(id).Value;

        var output = Assert.IsType<TerminalOutputEvent>(Assert.Single(events));
        Assert.Equal((id, "ola mundo", 9L), (output.Id, output.Data, output.Seq));
        Assert.Equal(("ola mundo", 9L), attach);
        Assert.Empty(_manager.TakeEvents());
        Assert.Equal(TerminalManager.NotFoundError, _manager.Attach("x").Error);
    }

    [Fact]
    public void Events_LargeOutputIsSplitIntoMessagesOfAtMost32KChars()
    {
        var manager = new TerminalManager(_pty, () => Shell);
        manager.Open(@"C:\src\app", null, null);
        for (var i = 0; i < 10; i++)
        {
            _pty.Last.Emit(new string('z', 10_000));
        }

        var events = manager.TakeEvents().Cast<TerminalOutputEvent>().ToList();

        Assert.All(events, e => Assert.True(e.Data.Length <= TerminalManager.MaxMessageChars));
        Assert.Equal(100_000, events.Sum(e => e.Data.Length));
        Assert.Equal(100_000, events[^1].Seq);
        manager.Dispose();
    }

    [Fact]
    public void Exit_MarksStopped_WithExitCode_AndEmitsExitEventAfterOutput()
    {
        var id = _manager.Open(@"C:\src\app", null, null).Value!.Id;
        _pty.Last.Emit("tchau");
        _pty.Last.Exit(3);

        var events = _manager.TakeEvents();
        var info = Assert.Single(_manager.List(NoProject));

        Assert.IsType<TerminalOutputEvent>(events[0]);
        Assert.Equal(new TerminalExitEvent(id, 3), events[1]);
        Assert.False(info.Running);
        Assert.Equal(3, info.ExitCode);
        Assert.Equal(0, _manager.RunningCount);
    }

    [Fact]
    public void Restart_KillsRunningTree_ClearsRing_AndStartsSameCommand()
    {
        var id = _manager.Open(@"C:\src\app", null, null).Value!.Id;
        var first = _pty.Last;
        first.Emit("antigo");

        var info = _manager.Restart(id).Value!;

        Assert.True(first.Killed);
        Assert.NotSame(first, _pty.Last);
        Assert.Equal(first.Info.CommandLine, _pty.Last.Info.CommandLine);
        Assert.True(info.Running);
        Assert.Equal(id, info.Id);
        Assert.Equal(string.Empty, _manager.Attach(id).Value.Data);
        _pty.Last.Emit("novo");
        Assert.Equal("novo", _manager.Attach(id).Value.Data);
    }

    [Fact]
    public void LateOutputFromOldSession_AfterRestart_IsIgnored()
    {
        var id = _manager.Open(@"C:\src\app", null, null).Value!.Id;
        var first = _pty.Last;
        _manager.Restart(id);

        first.Emit("fantasma");

        Assert.Equal(string.Empty, _manager.Attach(id).Value.Data);
    }

    [Fact]
    public void Close_KillsTreeAndRemoves()
    {
        var id = _manager.Open(@"C:\src\app", null, null).Value!.Id;
        var session = _pty.Last;

        Assert.True(_manager.Close(id).Ok);

        Assert.True(session.Killed);
        Assert.True(session.Disposed);
        Assert.Empty(_manager.List(NoProject));
        Assert.Equal(TerminalManager.NotFoundError, _manager.Close(id).Error);
    }

    [Fact]
    public void StartSaved_ReusesTheStoppedTerminalOfTheSameSaved()
    {
        var saved = new SavedProcess { Id = "s1", Name = "API", Command = "npm run dev" };
        var launch = new TerminalLaunch(@"C:\Windows\System32\cmd.exe", "\"C:\\Windows\\System32\\cmd.exe\" /s /k \"npm run dev\"", new Dictionary<string, string> { ["PORT"] = "3000" });

        var first = _manager.StartSaved(saved, launch, @"C:\src\api").Value!;
        Assert.Equal(TerminalManager.AlreadyRunningError, _manager.StartSaved(saved, launch, @"C:\src\api").Error);
        _pty.Last.Emit("rodou");
        _pty.Last.Exit(0);

        var second = _manager.StartSaved(saved, launch, @"C:\src\api").Value!;

        Assert.Equal(first.Terminal.Id, second.Terminal.Id);
        Assert.Single(_manager.List(NoProject));
        Assert.Equal("API", second.Terminal.Title);
        Assert.Equal("s1", second.Terminal.SavedId);
        Assert.Equal(_pty.Last.Pid, second.Process.Pid);
        Assert.Equal(_pty.Last.CreateTime, second.Process.CreateTime);
        Assert.Equal("3000", _pty.Last.Info.Environment["PORT"]);
        Assert.Equal(string.Empty, _manager.Attach(first.Terminal.Id).Value.Data); // aba limpa
        Assert.Equal(first.Terminal.Id, _manager.TerminalIdForSaved("s1"));
    }

    [Fact]
    public void CloseAll_KillsEverySession()
    {
        _manager.Open(@"C:\src\a", null, null);
        _manager.Open(@"C:\src\b", null, null);

        _manager.CloseAll();

        Assert.All(_pty.Sessions, s => Assert.True(s.Killed && s.Disposed));
        Assert.Empty(_manager.List(NoProject));
    }

    [Fact]
    public void PendingOutputIsBounded_WhenNobodyTakesEvents()
    {
        var manager = new TerminalManager(_pty, () => Shell);
        manager.Open(@"C:\src\app", null, null);
        var chunk = new string('q', 100_000);
        for (var i = 0; i < 30; i++)
        {
            _pty.Last.Emit(chunk); // 3 M caracteres no total
        }

        var events = manager.TakeEvents().Cast<TerminalOutputEvent>().ToList();

        Assert.True(events.Sum(e => e.Data.Length) <= TerminalManager.MaxPendingChars);
        Assert.Equal(3_000_000, events[^1].Seq); // o mais recente fica; a UI vê o salto do seq e usa termAttach
        manager.Dispose();
    }
}
