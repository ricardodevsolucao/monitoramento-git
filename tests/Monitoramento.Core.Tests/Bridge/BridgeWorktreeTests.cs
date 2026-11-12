using System.Text.Json;
using Monitoramento.Core.Bridge;
using Monitoramento.Core.Git;
using Monitoramento.Core.Terminal;
using Monitoramento.Core.Tests.Git;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Bridge;

/// <summary>Worktrees git pela ponte, com repositórios reais em pasta temporária (o git escreve só lá).</summary>
public sealed class BridgeWorktreeTests : IDisposable
{
    private readonly EngineHarness _h = new();

    public BridgeWorktreeTests()
    {
        Project = _h.Temp.Combine("proj");
        App = Path.Combine(Project, "app");
        InitRepo(App);
        Git(App, "branch", "feat");
    }

    private string Project { get; }

    private string App { get; }

    private BridgeRouter? _router;

    private BridgeRouter Router => _router ??= new BridgeRouter(_h.Engine);

    private bool HasGit => _h.GitDetails is not null;

    public void Dispose() => _h.Dispose();

    private static void InitRepo(string path)
    {
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", "main");
        Git(path, "config", "user.name", "Teste");
        Git(path, "config", "user.email", "teste@exemplo.com");
        Git(path, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(path, "readme.txt"), "ola");
        Directory.CreateDirectory(Path.Combine(path, "api"));
        File.WriteAllText(Path.Combine(path, "api", "server.txt"), "api");
        Git(path, "add", "-A");
        Git(path, "commit", "-q", "-m", "inicial");
    }

    private static string Git(string path, params string[] args) => GitTestRepo.RunGit(path, args);

    private async Task<JsonElement> SendAsync(string type, object? payload)
    {
        var message = JsonSerializer.Serialize(new { id = "w1", type, payload });
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

    private async Task SaveProjectAsync()
    {
        _h.Engine.KnownRepos.Register(App);
        Ok(await SendAsync("saveGitProject", new { project = new { id = "", name = "proj", folder = Project } }));
    }

    private static List<JsonElement> Worktrees(JsonElement details) => details.GetProperty("worktrees").EnumerateArray().ToList();

    // ---------- leitura ----------

    [Fact]
    public async Task Details_ListsWorktrees_MarksBranchInUse_AndRegistersThem()
    {
        if (!HasGit)
        {
            return;
        }

        var wt = _h.Temp.Combine("wt-feat");
        Git(App, "worktree", "add", wt, "feat");
        _h.Engine.KnownRepos.Register(App);

        var details = Ok(await SendAsync("gitDetails", new { repoRoot = App })).GetProperty("details");

        var list = Worktrees(details);
        Assert.Equal(2, list.Count);
        Assert.Equal(App, list[0].GetProperty("path").GetString());
        Assert.True(list[0].GetProperty("isMain").GetBoolean());
        Assert.Equal(wt, list[1].GetProperty("path").GetString());
        Assert.Equal("feat", list[1].GetProperty("branch").GetString());
        Assert.Equal(7, list[1].GetProperty("head").GetString()!.Length);
        Assert.False(list[1].GetProperty("isMain").GetBoolean());
        Assert.False(list[1].GetProperty("prunable").GetBoolean());
        var feat = details.GetProperty("branches").EnumerateArray().First(b => b.GetProperty("name").GetString() == "feat");
        Assert.Equal(wt, feat.GetProperty("worktreePath").GetString());
        var main = details.GetProperty("branches").EnumerateArray().First(b => b.GetProperty("name").GetString() == "main");
        Assert.Equal(JsonValueKind.Null, main.GetProperty("worktreePath").ValueKind);
        Assert.True(_h.Engine.KnownRepos.Contains(wt));

        var inWorktree = Ok(await SendAsync("gitDetails", new { repoRoot = wt })).GetProperty("details");
        Assert.Equal(App, inWorktree.GetProperty("repo").GetProperty("mainRepoRoot").GetString());
    }

    [Fact]
    public async Task Details_DropsWorktreeWhoseGitFilePointsElsewhere()
    {
        if (!HasGit)
        {
            return;
        }

        // entrada forjada em .git/worktrees: o "worktree" listado é uma pasta cujo .git aponta para OUTRO repositório
        var other = _h.Temp.Combine("outro");
        InitRepo(other);
        Git(other, "branch", "x");
        var foreignWt = _h.Temp.Combine("outro-x");
        Git(other, "worktree", "add", foreignWt, "x");
        var evil = Path.Combine(App, ".git", "worktrees", "evil");
        Directory.CreateDirectory(evil);
        File.WriteAllText(Path.Combine(evil, "gitdir"), Path.Combine(foreignWt, ".git").Replace('\\', '/') + "\n");
        File.WriteAllText(Path.Combine(evil, "commondir"), "../..\n");
        File.WriteAllText(Path.Combine(evil, "HEAD"), "ref: refs/heads/main\n");
        _h.Engine.KnownRepos.Register(App);

        var details = Ok(await SendAsync("gitDetails", new { repoRoot = App })).GetProperty("details");

        Assert.DoesNotContain(Worktrees(details), w => string.Equals(w.GetProperty("path").GetString(), foreignWt, StringComparison.OrdinalIgnoreCase));
        Assert.False(_h.Engine.KnownRepos.Contains(foreignWt));
    }

    // ---------- gitWorktreeAdd ----------

    [Fact]
    public async Task Add_ExistingBranch_DefaultPathNextToProject_PinnedAndListed()
    {
        if (!HasGit)
        {
            return;
        }

        await SaveProjectAsync();

        var data = Ok(await SendAsync("gitWorktreeAdd", new { repoRoot = App, branch = "feat" }));

        var expected = Path.Combine(_h.Temp.Path, "proj.worktrees", "app-feat");
        var worktree = data.GetProperty("worktree");
        Assert.Equal(expected, worktree.GetProperty("path").GetString());
        Assert.Equal("feat", worktree.GetProperty("branch").GetString());
        Assert.True(File.Exists(Path.Combine(expected, "readme.txt")));
        Assert.Contains(Worktrees(data.GetProperty("details")), w => w.GetProperty("path").GetString() == expected);
        Assert.Equal(0, data.GetProperty("copied").GetArrayLength());
        _h.Time.Advance(TimeSpan.FromHours(2));
        Assert.True(_h.Engine.KnownRepos.Contains(expected));
    }

    [Fact]
    public async Task Add_NewBranch_FromStartPoint_SlugWithSuffix_WhenFolderExists()
    {
        if (!HasGit)
        {
            return;
        }

        _h.Engine.KnownRepos.Register(App);
        var taken = Path.Combine(_h.Temp.Path, "proj.worktrees", "app-feature-login_x");
        Directory.CreateDirectory(taken);
        File.WriteAllText(Path.Combine(taken, "ocupado.txt"), "x");

        var worktree = Ok(await SendAsync("gitWorktreeAdd", new { repoRoot = App, newBranch = "feature/Login_X", startPoint = "feat" })).GetProperty("worktree");

        Assert.Equal(taken + "-2", worktree.GetProperty("path").GetString());
        Assert.Equal("feature/Login_X", worktree.GetProperty("branch").GetString());
        Assert.Equal(Git(App, "rev-parse", "feat"), Git(taken + "-2", "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Add_RemoteBranch_CreatesLocalTrackingBranch()
    {
        if (!HasGit)
        {
            return;
        }

        var bare = _h.Temp.Combine("remoto.git");
        Git(_h.Temp.Path, "init", "-q", "--bare", "-b", "main", bare);
        Git(App, "remote", "add", "origin", bare);
        Git(App, "push", "-q", "origin", "main:remota");
        Git(App, "fetch", "-q", "origin");
        _h.Engine.KnownRepos.Register(App);

        var worktree = Ok(await SendAsync("gitWorktreeAdd", new { repoRoot = App, branch = "origin/remota" })).GetProperty("worktree");

        Assert.Equal("remota", worktree.GetProperty("branch").GetString());
        Assert.Equal("origin/remota", Git(App, "rev-parse", "--abbrev-ref", "remota@{upstream}"));
    }

    [Fact]
    public async Task Add_BranchAlreadyInUse_IsRefused()
    {
        if (!HasGit)
        {
            return;
        }

        _h.Engine.KnownRepos.Register(App);

        Assert.Contains("já está em uso", Error(await SendAsync("gitWorktreeAdd", new { repoRoot = App, branch = "main" })), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_UnknownRepo_IsRefused()
    {
        Assert.Equal(GitDetailsService.UnknownRepoError, Error(await SendAsync("gitWorktreeAdd", new { repoRoot = App, branch = "feat" })));
    }

    public static TheoryData<string> BadPaths() => new()
    {
        @"\\servidor\share\wt",
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    };

    [Theory]
    [MemberData(nameof(BadPaths))]
    public async Task Add_NonLocalOrBroadPath_IsRefused(string path)
    {
        if (!HasGit)
        {
            return;
        }

        _h.Engine.KnownRepos.Register(App);

        Assert.StartsWith("Caminho do worktree", Error(await SendAsync("gitWorktreeAdd", new { repoRoot = App, branch = "feat", path })), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_PathRules_ParentMissing_NotEmpty_InsideRepo_ThroughJunction()
    {
        if (!HasGit)
        {
            return;
        }

        _h.Engine.KnownRepos.Register(App);
        var notEmpty = _h.Temp.Combine("cheia");
        Directory.CreateDirectory(notEmpty);
        File.WriteAllText(Path.Combine(notEmpty, "a.txt"), "a");
        var junction = _h.Temp.Combine("juncao");
        Directory.CreateDirectory(_h.Temp.Combine("alvo"));
        GitConfigSafetyTests.Junction(junction, _h.Temp.Combine("alvo"));

        var parentMissing = Error(await SendAsync("gitWorktreeAdd", new { repoRoot = App, branch = "feat", path = _h.Temp.Combine("nao", "existe", "wt") }));
        var full = Error(await SendAsync("gitWorktreeAdd", new { repoRoot = App, branch = "feat", path = notEmpty }));
        var insideRepo = Error(await SendAsync("gitWorktreeAdd", new { repoRoot = App, branch = "feat", path = Path.Combine(App, "api", "wt") }));
        var linked = Error(await SendAsync("gitWorktreeAdd", new { repoRoot = App, branch = "feat", path = Path.Combine(junction, "wt") }));

        Assert.Contains("pasta-mãe", parentMissing, StringComparison.Ordinal);
        Assert.Contains("não está vazia", full, StringComparison.Ordinal);
        Assert.Contains("dentro de outro repositório", insideRepo, StringComparison.Ordinal);
        Assert.Contains("link", linked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_CopyEnv_CopiesUntrackedEnvFilesOnly()
    {
        if (!HasGit)
        {
            return;
        }

        File.WriteAllText(Path.Combine(App, ".env.prod"), "PROD=1");
        Git(App, "add", ".env.prod");
        Git(App, "commit", "-q", "-m", "env versionado");
        File.WriteAllText(Path.Combine(App, ".env"), "A=1");
        File.WriteAllText(Path.Combine(App, ".env.local"), "B=2");
        File.WriteAllText(Path.Combine(App, ".env.example"), "C=3");
        File.WriteAllText(Path.Combine(App, ".env.sample"), "D=4");
        File.WriteAllText(Path.Combine(App, ".envrc"), "E=5");
        _h.Engine.KnownRepos.Register(App);

        var data = Ok(await SendAsync("gitWorktreeAdd", new { repoRoot = App, branch = "feat", copyEnv = true }));

        var path = data.GetProperty("worktree").GetProperty("path").GetString()!;
        Assert.Equal([".env", ".env.local"], data.GetProperty("copied").EnumerateArray().Select(e => e.GetString()).Order());
        Assert.Equal("A=1", File.ReadAllText(Path.Combine(path, ".env")));
        Assert.False(File.Exists(Path.Combine(path, ".env.example")));
        Assert.False(File.Exists(Path.Combine(path, ".envrc")));
    }

    // ---------- gitWorktreeRemove / gitWorktreePrune ----------

    private async Task<string> AddAsync(string branch = "feat")
    {
        _h.Engine.KnownRepos.Register(App);
        return Ok(await SendAsync("gitWorktreeAdd", new { repoRoot = App, branch })).GetProperty("worktree").GetProperty("path").GetString()!;
    }

    [Fact]
    public async Task Remove_Main_IsRefused()
    {
        if (!HasGit)
        {
            return;
        }

        await AddAsync();

        Assert.Equal(GitActionService.RemoveMainWorktreeError, Error(await SendAsync("gitWorktreeRemove", new { repoRoot = App, path = App, force = true })));
    }

    [Fact]
    public async Task Remove_DirtyWithoutForce_AsksConfirmation_ThenForceRemovesAndUnpins()
    {
        if (!HasGit)
        {
            return;
        }

        var path = await AddAsync();
        File.WriteAllText(Path.Combine(path, "novo.txt"), "x");

        var dirtyReply = await SendAsync("gitWorktreeRemove", new { repoRoot = App, path, force = false });
        var dirty = Error(dirtyReply);
        var removed = Ok(await SendAsync("gitWorktreeRemove", new { repoRoot = App, path, force = true })).GetProperty("details");

        Assert.Equal(GitActionService.WorktreeDirtyError, dirty);
        Assert.Equal("worktreeDirty", dirtyReply.GetProperty("code").GetString());
        Assert.False(Directory.Exists(path));
        Assert.Single(Worktrees(removed));
        _h.Time.Advance(TimeSpan.FromHours(2));
        Assert.False(_h.Engine.KnownRepos.Contains(path));
    }

    [Fact]
    public async Task Remove_WithTerminalOrProcessInside_IsRefused()
    {
        if (!HasGit)
        {
            return;
        }

        var path = await AddAsync();
        _h.Engine.Terminals.Open(path, null, null);

        var terminalReply = await SendAsync("gitWorktreeRemove", new { repoRoot = App, path, force = true });
        var withTerminal = Error(terminalReply);
        _h.Engine.Terminals.CloseAll();
        _h.AddProcess(321, "node.exe", 1000, "node server.js", Path.Combine(path, "api"));
        _h.Engine.Sample();
        var processReply = await SendAsync("gitWorktreeRemove", new { repoRoot = App, path, force = true });
        var withProcess = Error(processReply);

        Assert.Equal("worktreeInUse", terminalReply.GetProperty("code").GetString());
        Assert.Equal("worktreeInUse", processReply.GetProperty("code").GetString());
        var mainReply = await SendAsync("gitWorktreeRemove", new { repoRoot = App, path = App, force = true });
        Assert.False(mainReply.TryGetProperty("code", out _)); // erros sem código não trazem o campo

        Assert.StartsWith("Feche os terminais/processos rodando no worktree antes de remover.", withTerminal, StringComparison.Ordinal);
        Assert.Contains("terminal", withTerminal, StringComparison.Ordinal);
        Assert.Contains("node.exe (pid 321)", withProcess, StringComparison.Ordinal);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task Prune_RemovesWorktreeWhoseFolderWasDeleted()
    {
        if (!HasGit)
        {
            return;
        }

        var path = await AddAsync();
        new TempDirectoryCleaner(path).Delete();

        var before = Ok(await SendAsync("gitDetails", new { repoRoot = App, force = true })).GetProperty("details");
        var after = Ok(await SendAsync("gitWorktreePrune", new { repoRoot = App })).GetProperty("details");

        Assert.True(Worktrees(before).Single(w => !w.GetProperty("isMain").GetBoolean()).GetProperty("prunable").GetBoolean());
        Assert.Single(Worktrees(after));
    }

    // ---------- termRunSavedIn ----------

    private async Task<string> SaveProcessAsync(string? workingDirectory)
    {
        var saved = new { id = "", name = "API", command = "npm run dev", workingDirectory, env = new Dictionary<string, string> { ["PORT"] = "3000" } };
        return Ok(await SendAsync("saveProcess", new { saved })).GetProperty("saved").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task RunSavedIn_UsesSameRelativeFolderInsideWorktree()
    {
        if (!HasGit)
        {
            return;
        }

        var path = await AddAsync();
        var savedId = await SaveProcessAsync(Path.Combine(App, "api"));

        var terminal = Ok(await SendAsync("termRunSavedIn", new { savedId, worktreePath = path, cols = 100, rows = 30 })).GetProperty("terminal");

        var started = _h.Pty.Last.Info;
        Assert.Equal(Path.Combine(path, "api"), started.WorkingDirectory);
        Assert.EndsWith("/s /k \"npm run dev\"", started.CommandLine, StringComparison.Ordinal);
        Assert.Equal("3000", started.Environment["PORT"]);
        Assert.Equal("API ⎇ feat", terminal.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, terminal.GetProperty("savedId").ValueKind);
        Assert.Equal(Path.Combine(App, "api"), _h.Engine.Config.Saved[0].WorkingDirectory); // o salvo não muda
    }

    [Fact]
    public async Task RunSavedIn_RefusesPathThatIsNotAWorktreeOfTheSavedRepo()
    {
        if (!HasGit)
        {
            return;
        }

        await AddAsync();
        var savedId = await SaveProcessAsync(Path.Combine(App, "api"));
        var other = _h.Temp.Combine("outro");
        InitRepo(other);
        _h.Engine.KnownRepos.Register(other);

        var plainFolder = Error(await SendAsync("termRunSavedIn", new { savedId, worktreePath = _h.Temp.Path }));
        var otherRepo = Error(await SendAsync("termRunSavedIn", new { savedId, worktreePath = other }));
        var unknownSaved = Error(await SendAsync("termRunSavedIn", new { savedId = "nao-existe", worktreePath = App }));

        Assert.Equal(TerminalErrors.NotAWorktreeOfSavedRepo, plainFolder);
        Assert.Equal(TerminalErrors.NotAWorktreeOfSavedRepo, otherRepo);
        Assert.Equal("Processo salvo não encontrado.", unknownSaved);
    }

    // ---------- workspace ----------

    [Fact]
    public async Task Workspace_WorktreeTab_IsKeptPinnedAndPersisted()
    {
        if (!HasGit)
        {
            return;
        }

        var path = await AddAsync();

        var tab = Ok(await SendAsync("setGitWorkspace", new { workspace = new { tabs = new[] { new { folder = path, name = "proj · app ⎇ feat", repos = new[] { path }, worktreeOf = App } } } }))
            .GetProperty("workspace").GetProperty("tabs")[0];

        Assert.Equal(App, tab.GetProperty("worktreeOf").GetString());
        Assert.Equal(App, _h.Engine.Config.GitWorkspace.Tabs[0].WorktreeOf);
        _h.Time.Advance(TimeSpan.FromHours(2));
        Assert.True(_h.Engine.KnownRepos.Contains(path));
    }

    /// <summary>Apaga uma pasta com arquivos somente leitura do git.</summary>
    private sealed class TempDirectoryCleaner(string path)
    {
        public void Delete()
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
    }
}
