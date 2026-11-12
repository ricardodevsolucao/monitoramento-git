using System.Text.Json;
using Monitoramento.Core.Bridge;
using Monitoramento.Core.Models;
using Monitoramento.Core.Terminal;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Bridge;

/// <summary>Terminais embutidos pela ponte (pseudo-console falso): aceitação de pasta, ids, salvos em modo terminal e pushes.</summary>
public sealed class BridgeTerminalTests : IDisposable
{
    private const string UnknownFolderError = "Pasta não reconhecida: abra-a a partir de um processo ou repositório já detectado.";

    private readonly EngineHarness _h = new();

    public BridgeTerminalTests()
    {
        Workspace = _h.Temp.Combine("gsintegra");
        App = Path.Combine(Workspace, "app");
        Directory.CreateDirectory(Path.Combine(App, ".git"));
        File.WriteAllText(Path.Combine(App, ".git", "HEAD"), "ref: refs/heads/main\n");
    }

    private string Workspace { get; }

    private string App { get; }

    private BridgeRouter Router { get; set; } = null!;

    public void Dispose() => _h.Dispose();

    private async Task<JsonElement> SendAsync(string type, object? payload)
    {
        Router ??= new BridgeRouter(_h.Engine);
        var message = JsonSerializer.Serialize(new { id = "t1", type, payload });
        return JsonDocument.Parse(await Router.HandleMessageAsync(message)).RootElement.Clone();
    }

    private static JsonElement Ok(JsonElement reply)
    {
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        return reply.GetProperty("data");
    }

    private static string Error(JsonElement reply)
    {
        Assert.False(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        return reply.GetProperty("error").GetString()!;
    }

    private void ProcessIn(string folder)
    {
        _h.AddProcess(100, "code.exe", 1000, "code .", folder);
        _h.Engine.Sample();
    }

    private async Task<string> OpenAsync(string folder)
    {
        ProcessIn(folder);
        return Ok(await SendAsync("termOpen", new { folder, cols = 100, rows = 30 })).GetProperty("terminal").GetProperty("id").GetString()!;
    }

    // ---------- termOpen: mesma regra de pasta do GitProjectService ----------

    [Theory]
    [InlineData(@"\\servidor\share")]
    [InlineData("relativa")]
    [InlineData(null)]
    public async Task TermOpen_NonLocalFolder_IsRefused(string? folder)
    {
        Assert.Equal(UnknownFolderError, Error(await SendAsync("termOpen", new { folder, cols = 80, rows = 24 })));
        Assert.Empty(_h.Pty.Sessions);
    }

    [Fact]
    public async Task TermOpen_UnknownLocalFolder_IsRefused()
    {
        Assert.Equal(UnknownFolderError, Error(await SendAsync("termOpen", new { folder = Workspace, cols = 80, rows = 24 })));
        Assert.Empty(_h.Pty.Sessions);
    }

    [Fact]
    public async Task TermOpen_BroadFolder_IsRefusedEvenAsProcessFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ProcessIn(profile);

        Assert.StartsWith("Pasta ampla demais", Error(await SendAsync("termOpen", new { folder = profile })), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TermOpen_AcceptedFolder_StartsShellThere_AndUiCannotChooseTheCommand()
    {
        ProcessIn(Workspace);

        var terminal = Ok(await SendAsync("termOpen", new { folder = Workspace, cols = 90, rows = 25, command = "calc.exe" })).GetProperty("terminal");

        var started = _h.Pty.Last.Info;
        Assert.Equal(@"C:\pwsh\pwsh.exe", started.ApplicationName);
        Assert.Equal(Workspace, started.WorkingDirectory);
        Assert.Equal((90, 25), (started.Cols, started.Rows));
        Assert.Equal(Workspace, terminal.GetProperty("folder").GetString());
        Assert.Equal("gsintegra", terminal.GetProperty("title").GetString());
        Assert.True(terminal.GetProperty("running").GetBoolean());
        Assert.Equal(JsonValueKind.Null, terminal.GetProperty("savedId").ValueKind);
        Assert.Equal(JsonValueKind.Null, terminal.GetProperty("exitCode").ValueKind);
    }

    [Fact]
    public async Task TermOpen_InsideSavedProject_ReportsProjectFolder()
    {
        _h.Engine.KnownRepos.Register(App);
        Ok(await SendAsync("saveGitProject", new { project = new { id = "", name = "g", folder = Workspace } }));

        var terminal = Ok(await SendAsync("termOpen", new { folder = App })).GetProperty("terminal");
        var snapshot = Ok(await SendAsync("ready", null)).GetProperty("snapshot");

        Assert.Equal(Workspace, terminal.GetProperty("projectFolder").GetString());
        Assert.Equal(Workspace, snapshot.GetProperty("terminals")[0].GetProperty("projectFolder").GetString());
    }

    [Fact]
    public async Task TermOpen_WithoutConPty_IsRefusedInPortuguese()
    {
        _h.Pty.IsSupported = false;
        ProcessIn(Workspace);

        Assert.Equal(TerminalManager.UnsupportedError, Error(await SendAsync("termOpen", new { folder = Workspace })));
    }

    // ---------- input / resize / attach / close ----------

    [Fact]
    public async Task UnknownIds_AreRefused()
    {
        Assert.Equal(TerminalManager.NotFoundError, Error(await SendAsync("termInput", new { id = "nao-existe", data = "x" })));
        Assert.Equal(TerminalManager.NotFoundError, Error(await SendAsync("termResize", new { id = "nao-existe", cols = 80, rows = 24 })));
        Assert.Equal(TerminalManager.NotFoundError, Error(await SendAsync("termAttach", new { id = "nao-existe" })));
        Assert.Equal(TerminalManager.NotFoundError, Error(await SendAsync("termRestart", new { id = "nao-existe" })));
        Assert.Equal(TerminalManager.NotFoundError, Error(await SendAsync("termClose", new { id = "nao-existe" })));
    }

    [Fact]
    public async Task Input_Resize_Attach_Close_Flow()
    {
        var id = await OpenAsync(Workspace);
        var session = _h.Pty.Last;

        Ok(await SendAsync("termInput", new { id, data = "git status\r" }));
        Ok(await SendAsync("termResize", new { id, cols = 1, rows = 1000 }));
        session.Emit("saída");
        var attach = Ok(await SendAsync("termAttach", new { id }));
        Ok(await SendAsync("termClose", new { id }));

        Assert.Equal(["git status\r"], session.Written);
        Assert.Equal((20, 200), session.LastResize);
        Assert.Equal("saída", attach.GetProperty("data").GetString());
        Assert.Equal(5, attach.GetProperty("seq").GetInt64());
        Assert.True(session.Killed);
        Assert.Equal(0, Ok(await SendAsync("ready", null)).GetProperty("snapshot").GetProperty("terminals").GetArrayLength());
    }

    [Fact]
    public async Task Input_Over64KB_IsRefused()
    {
        var id = await OpenAsync(Workspace);

        Assert.Equal(TerminalManager.InputTooLargeError, Error(await SendAsync("termInput", new { id, data = new string('a', 64 * 1024 + 1) })));
    }

    [Fact]
    public async Task Restart_Shell_KeepsId_AndStartsNewSession()
    {
        var id = await OpenAsync(Workspace);
        var first = _h.Pty.Last;

        var terminal = Ok(await SendAsync("termRestart", new { id })).GetProperty("terminal");

        Assert.Equal(id, terminal.GetProperty("id").GetString());
        Assert.True(first.Killed);
        Assert.Equal(2, _h.Pty.Sessions.Count);
    }

    // ---------- pushes ----------

    [Fact]
    public async Task TerminalMessages_HaveContractShape()
    {
        var id = await OpenAsync(Workspace);
        _h.Pty.Last.Emit("oi\u001b[0m");
        _h.Pty.Last.Exit(2);

        var messages = Router.TakeTerminalMessages().Select(m => JsonDocument.Parse(m).RootElement.Clone()).ToList();

        Assert.Equal("termOutput", messages[0].GetProperty("type").GetString());
        var output = messages[0].GetProperty("payload");
        Assert.Equal(id, output.GetProperty("id").GetString());
        Assert.Equal("oi\u001b[0m", output.GetProperty("data").GetString());
        Assert.Equal(6, output.GetProperty("seq").GetInt64());
        Assert.Equal("termExit", messages[1].GetProperty("type").GetString());
        Assert.Equal(2, messages[1].GetProperty("payload").GetProperty("exitCode").GetInt32());
        Assert.Empty(Router.TakeTerminalMessages());
    }

    // ---------- processo salvo em modo terminal ----------

    private async Task<string> SaveAsync(string? launchMode)
    {
        var saved = launchMode is null
            ? (object)new { id = "", name = "API", command = "npm run dev", workingDirectory = App, env = new Dictionary<string, string> { ["PORT"] = "3000" } }
            : new { id = "", name = "API", command = "npm run dev", workingDirectory = App, env = new Dictionary<string, string> { ["PORT"] = "3000" }, launchMode };
        return Ok(await SendAsync("saveProcess", new { saved })).GetProperty("saved").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task SaveProcess_WithoutLaunchMode_DefaultsToTerminal()
    {
        await SaveAsync(null);

        Assert.Equal(LaunchModes.Terminal, Assert.Single(_h.Engine.Config.Saved).LaunchMode);
    }

    [Fact]
    public async Task StartSaved_TerminalMode_RunsCmdSlashKInTerminal_WithEnvAndTerminalId()
    {
        var id = await SaveAsync("terminal");

        var state = Ok(await SendAsync("startSaved", new { id })).GetProperty("state");
        var snapshot = Ok(await SendAsync("ready", null)).GetProperty("snapshot");

        var started = _h.Pty.Last.Info;
        Assert.EndsWith("cmd.exe\" /s /k \"npm run dev\"", started.CommandLine, StringComparison.Ordinal);
        Assert.Equal(App, started.WorkingDirectory);
        Assert.Equal("3000", started.Environment["PORT"]);
        Assert.Empty(_h.Starter.Launched);
        var terminalId = state.GetProperty("terminalId").GetString();
        Assert.False(string.IsNullOrEmpty(terminalId));
        Assert.True(state.GetProperty("running").GetBoolean());
        Assert.Equal([_h.Pty.Last.Pid], state.GetProperty("pids").EnumerateArray().Select(p => p.GetInt32()));
        var terminal = snapshot.GetProperty("terminals")[0];
        Assert.Equal(terminalId, terminal.GetProperty("id").GetString());
        Assert.Equal(id, terminal.GetProperty("savedId").GetString());
        Assert.Equal("API", terminal.GetProperty("title").GetString());
        Assert.Equal(terminalId, snapshot.GetProperty("saved")[0].GetProperty("terminalId").GetString());
    }

    [Fact]
    public async Task StopSaved_TerminalMode_KillsTree_TerminalStaysWithExitCode_ThenStartReusesTab()
    {
        var id = await SaveAsync("terminal");
        var terminalId = Ok(await SendAsync("startSaved", new { id })).GetProperty("state").GetProperty("terminalId").GetString();
        var first = _h.Pty.Last;

        var stopped = Ok(await SendAsync("stopSaved", new { id })).GetProperty("state");
        var afterStop = Ok(await SendAsync("ready", null)).GetProperty("snapshot").GetProperty("terminals")[0];
        var restarted = Ok(await SendAsync("startSaved", new { id })).GetProperty("state");

        Assert.True(first.Killed);
        Assert.False(stopped.GetProperty("running").GetBoolean());
        Assert.False(afterStop.GetProperty("running").GetBoolean());
        Assert.Equal(1, afterStop.GetProperty("exitCode").GetInt32());
        Assert.Equal(terminalId, restarted.GetProperty("terminalId").GetString());
        Assert.Single(Ok(await SendAsync("ready", null)).GetProperty("snapshot").GetProperty("terminals").EnumerateArray());
    }

    [Fact]
    public async Task TermRestart_OfSavedTerminal_RestartsTheSavedCommandInSameTab()
    {
        var id = await SaveAsync("terminal");
        var terminalId = Ok(await SendAsync("startSaved", new { id })).GetProperty("state").GetProperty("terminalId").GetString();

        var terminal = Ok(await SendAsync("termRestart", new { id = terminalId })).GetProperty("terminal");

        Assert.Equal(terminalId, terminal.GetProperty("id").GetString());
        Assert.True(terminal.GetProperty("running").GetBoolean());
        Assert.Equal(2, _h.Pty.Sessions.Count);
        Assert.EndsWith("/s /k \"npm run dev\"", _h.Pty.Last.Info.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartSaved_TerminalModeWithoutConPty_FallsBackToWindow()
    {
        _h.Pty.IsSupported = false;
        var id = await SaveAsync("terminal");

        var state = Ok(await SendAsync("startSaved", new { id })).GetProperty("state");

        var spec = Assert.Single(_h.Starter.Launched);
        Assert.True(spec.NewConsole);
        Assert.Contains("/s /k", spec.CommandLine, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("terminalId").ValueKind);
    }

    [Theory]
    [InlineData("window", true, "/k")]
    [InlineData("hidden", false, "/c")]
    public async Task StartSaved_WindowAndHidden_StillUseSeparateProcess(string mode, bool newConsole, string slash)
    {
        var id = await SaveAsync(mode);

        Ok(await SendAsync("startSaved", new { id }));

        var spec = Assert.Single(_h.Starter.Launched);
        Assert.Equal(newConsole, spec.NewConsole);
        Assert.Contains("/s " + slash, spec.CommandLine, StringComparison.Ordinal);
        Assert.Empty(_h.Pty.Sessions);
    }

    [Fact]
    public void EngineDispose_ClosesAllTerminals()
    {
        ProcessIn(Workspace);
        _h.Engine.Terminals.Open(Workspace, null, null);
        var session = _h.Pty.Last;

        _h.Engine.Dispose();

        Assert.True(session.Killed);
        Assert.True(session.Disposed);
    }

    [Fact]
    public void RunningTerminals_CountsOnlyRunning()
    {
        ProcessIn(Workspace);
        _h.Engine.Terminals.Open(Workspace, null, null);
        _h.Engine.Terminals.Open(App, null, null);
        _h.Pty.Sessions[0].Exit(0);

        Assert.Equal(1, _h.Engine.RunningTerminals);
    }

    // ---------- M1: aba de salvo excluído ----------

    [Fact]
    public async Task DeleteSaved_UnlinksTheTab_AndRestartAsksToClose()
    {
        var id = await SaveAsync("terminal");
        var terminalId = Ok(await SendAsync("startSaved", new { id })).GetProperty("state").GetProperty("terminalId").GetString();
        Ok(await SendAsync("stopSaved", new { id }));

        Ok(await SendAsync("deleteSaved", new { id }));
        var restart = Error(await SendAsync("termRestart", new { id = terminalId }));
        _h.Engine.Sample(); // o snapshot do ready é o último coletado
        var terminal = Ok(await SendAsync("ready", null)).GetProperty("snapshot").GetProperty("terminals")[0];

        Assert.Equal("O processo salvo foi excluído; feche este terminal.", restart);
        Assert.Equal(JsonValueKind.Null, terminal.GetProperty("savedId").ValueKind);
        Assert.Single(_h.Pty.Sessions);
    }

    // ---------- M3: repositório cuja config pode executar programas ----------

    [Fact]
    public async Task TermOpen_RepoWithUnsafeConfig_NeedsConfirmation()
    {
        if (_h.GitDetails is null)
        {
            return; // a checagem usa o parser de config do git
        }

        File.WriteAllText(Path.Combine(App, ".git", "config"), "[core]\n\tfsmonitor = true\n");
        _h.Engine.KnownRepos.Register(App);

        var refused = await SendAsync("termOpen", new { folder = App });
        var parentRefused = await SendAsync("termOpen", new { folder = Workspace, confirmUnsafe = false });
        var confirmed = Ok(await SendAsync("termOpen", new { folder = App, confirmUnsafe = true }));

        Assert.Equal("unsafeRepo", refused.GetProperty("code").GetString());
        Assert.StartsWith("A configuração git deste repositório pode executar programas (", Error(refused), StringComparison.Ordinal);
        Assert.EndsWith("Abrir o terminal mesmo assim?", Error(refused), StringComparison.Ordinal);
        Assert.Contains("fsmonitor", Error(refused), StringComparison.Ordinal);
        Assert.Equal("unsafeRepo", parentRefused.GetProperty("code").GetString()); // pasta-mãe: repos filhos também contam
        Assert.True(confirmed.GetProperty("terminal").GetProperty("running").GetBoolean());
        Assert.Single(_h.Pty.Sessions);
    }

    [Fact]
    public async Task ErrorsWithoutCode_DoNotCarryCodeField()
    {
        var reply = await SendAsync("termClose", new { id = "x" });

        Assert.False(reply.TryGetProperty("code", out _));
    }
}
