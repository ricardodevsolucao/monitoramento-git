using System.Text.Json;
using Monitoramento.Core.Bridge;
using Monitoramento.Core.Models;
using Monitoramento.Core.Tests.Git;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Bridge;

/// <summary>
/// Projetos git salvos e workspace git pela ponte. Ponto principal: a UI não consegue fazer o app olhar (nem rodar
/// git) num caminho arbitrário — toda pasta passa pela regra de aceitação e por LocalPath.
/// </summary>
public sealed class BridgeGitProjectTests : IDisposable
{
    private const string UnknownFolderError = "Pasta não reconhecida: abra-a a partir de um processo ou repositório já detectado.";
    private const string NoReposError = "Nenhum repositório git nessa pasta.";

    private readonly EngineHarness _h = new();

    public BridgeGitProjectTests()
    {
        Workspace = _h.Temp.Combine("gsintegra");
        App = FakeRepo(Workspace, "app");
        Api = FakeRepo(Workspace, "api");
        Directory.CreateDirectory(Path.Combine(Workspace, "docs"));
    }

    private string Workspace { get; }

    private string App { get; }

    private string Api { get; }

    public void Dispose() => _h.Dispose();

    private BridgeRouter Router => new(_h.Engine);

    /// <summary>Repositório mínimo para o localizador (pasta .git com HEAD; o git não é chamado).</summary>
    private static string FakeRepo(params string[] parts)
    {
        var path = Path.Combine(parts);
        var gitDir = Directory.CreateDirectory(Path.Combine(path, ".git")).FullName;
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");
        return path;
    }

    private async Task<JsonElement> SendAsync(string type, object? payload)
    {
        var message = JsonSerializer.Serialize(new { id = "p1", type, payload });
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

    private static List<string?> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()).ToList();

    /// <summary>Processo com o repositório na pasta de trabalho: o motor registra o repo como conhecido.</summary>
    private void SeeRepoInProcess(string repo, int pid = 100)
    {
        _h.Git.ByDirectory[repo] = new GitInfo(repo, Path.GetFileName(repo), "main", null, null, null);
        _h.AddProcess(pid, "node.exe", 1000, "node app.js", repo);
        _h.Engine.Sample();
    }

    private void WriteConfig(string json) => File.WriteAllText(_h.ConfigPath, json);

    // ---------- gitProjectDraft ----------

    [Theory]
    [InlineData(@"\\servidor\share\gsintegra")]
    [InlineData(@"\\?\C:\gsintegra")]
    [InlineData("relativa")]
    [InlineData("")]
    public async Task Draft_NonLocalPath_IsRefused(string path)
    {
        var error = Error(await SendAsync("gitProjectDraft", new { path }));

        Assert.Equal(UnknownFolderError, error);
    }

    [Fact]
    public async Task Draft_UnknownLocalFolderWithRepos_IsRefused()
    {
        var error = Error(await SendAsync("gitProjectDraft", new { path = Workspace }));

        Assert.Equal(UnknownFolderError, error);
    }

    [Fact]
    public async Task Draft_KnownRepoWithSiblingRepos_SuggestsParentFolder()
    {
        SeeRepoInProcess(App);

        var data = Ok(await SendAsync("gitProjectDraft", new { path = App }));

        var project = data.GetProperty("project");
        Assert.Equal("", project.GetProperty("id").GetString());
        Assert.Equal(Workspace, project.GetProperty("folder").GetString());
        Assert.Equal("gsintegra", project.GetProperty("name").GetString());
        Assert.Equal([Api, App], Strings(project.GetProperty("repos")));
        Assert.Equal([Api, App], data.GetProperty("repos").EnumerateArray().Select(r => r.GetProperty("repoRoot").GetString()).ToList());
    }

    [Fact]
    public async Task Draft_KnownRepoWithoutSiblings_SuggestsItself()
    {
        var alone = FakeRepo(_h.Temp.Path, "sozinho", "repo");
        SeeRepoInProcess(alone);

        var project = Ok(await SendAsync("gitProjectDraft", new { path = alone })).GetProperty("project");

        Assert.Equal(alone, project.GetProperty("folder").GetString());
        Assert.Equal("repo", project.GetProperty("name").GetString());
        Assert.Equal([alone], Strings(project.GetProperty("repos")));
    }

    [Fact]
    public async Task Draft_ProcessWorkingDirectoryThatIsNotARepo_ListsChildRepos()
    {
        _h.AddProcess(100, "code.exe", 1000, "code .", Workspace);
        _h.Engine.Sample();

        var project = Ok(await SendAsync("gitProjectDraft", new { path = Workspace + "\\" })).GetProperty("project");

        Assert.Equal(Workspace, project.GetProperty("folder").GetString());
        Assert.Equal([Api, App], Strings(project.GetProperty("repos")));
    }

    [Fact]
    public async Task Draft_ParentOfKnownRepo_IsAccepted()
    {
        _h.Engine.KnownRepos.Register(Api);

        var project = Ok(await SendAsync("gitProjectDraft", new { path = Workspace })).GetProperty("project");

        Assert.Equal([Api, App], Strings(project.GetProperty("repos")));
    }

    [Fact]
    public async Task Draft_GrandparentOfKnownRepo_IsRefused()
    {
        _h.Engine.KnownRepos.Register(Api);

        Assert.Equal(UnknownFolderError, Error(await SendAsync("gitProjectDraft", new { path = _h.Temp.Path })));
    }

    [Fact]
    public async Task Draft_AcceptedFolderWithoutRepos_ReturnsPortugueseError()
    {
        var empty = _h.Temp.Combine("vazia");
        Directory.CreateDirectory(empty);
        _h.AddProcess(100, "cmd.exe", 1000, "cmd", empty);
        _h.Engine.Sample();

        Assert.Equal(NoReposError, Error(await SendAsync("gitProjectDraft", new { path = empty })));
    }

    // ---------- saveGitProject ----------

    [Fact]
    public async Task Save_New_RecomputesReposIgnoringUi_AndPersists()
    {
        SeeRepoInProcess(App);

        var project = Ok(await SendAsync("saveGitProject", new
        {
            project = new { id = "", name = "  Integra  ", folder = Workspace, repos = new[] { @"C:\Windows" } },
        })).GetProperty("project");

        Assert.True(Guid.TryParse(project.GetProperty("id").GetString(), out _));
        Assert.Equal("Integra", project.GetProperty("name").GetString());
        Assert.Equal([Api, App], Strings(project.GetProperty("repos")));

        var saved = Assert.Single(_h.Engine.Config.GitProjects);
        Assert.Equal([Api, App], saved.Repos);
        Assert.Contains("\"gitProjects\"", File.ReadAllText(_h.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_UnknownFolder_IsRefused()
    {
        var error = Error(await SendAsync("saveGitProject", new { project = new { id = "", name = "x", folder = Workspace } }));

        Assert.Equal(UnknownFolderError, error);
        Assert.Empty(_h.Engine.Config.GitProjects);
    }

    [Fact]
    public async Task Save_UncFolder_IsRefused()
    {
        SeeRepoInProcess(App);

        var error = Error(await SendAsync("saveGitProject", new { project = new { id = "", name = "x", folder = @"\\servidor\gsintegra" } }));

        Assert.Equal(UnknownFolderError, error);
    }

    [Fact]
    public async Task Save_EmptyName_UsesFolderName_AndTooLongNameIsRefused()
    {
        SeeRepoInProcess(App);

        var project = Ok(await SendAsync("saveGitProject", new { project = new { id = "", name = "   ", folder = Workspace } })).GetProperty("project");
        var error = Error(await SendAsync("saveGitProject", new { project = new { id = "", name = new string('a', 81), folder = Workspace } }));

        Assert.Equal("gsintegra", project.GetProperty("name").GetString());
        Assert.Equal("O nome do projeto pode ter no máximo 80 caracteres.", error);
    }

    [Fact]
    public async Task Save_DuplicateFolder_UpdatesExistingInsteadOfDuplicating()
    {
        SeeRepoInProcess(App);
        var first = Ok(await SendAsync("saveGitProject", new { project = new { id = "", name = "A", folder = Workspace } })).GetProperty("project");

        var second = Ok(await SendAsync("saveGitProject", new { project = new { id = "", name = "B", folder = Workspace.ToUpperInvariant() + "\\" } })).GetProperty("project");

        Assert.Equal(first.GetProperty("id").GetString(), second.GetProperty("id").GetString());
        var only = Assert.Single(_h.Engine.Config.GitProjects);
        Assert.Equal("B", only.Name);
    }

    [Fact]
    public async Task Save_ExistingId_RenamesWithItsOwnFolder_EvenWhenNothingRuns()
    {
        WriteConfig($$"""{ "gitProjects": [ { "id": "p1", "name": "Velho", "folder": {{JsonSerializer.Serialize(Workspace)}}, "repos": [] } ] }""");

        var project = Ok(await SendAsync("saveGitProject", new { project = new { id = "p1", name = "Novo", folder = Workspace } })).GetProperty("project");

        Assert.Equal("p1", project.GetProperty("id").GetString());
        Assert.Equal("Novo", project.GetProperty("name").GetString());
        Assert.Equal([Api, App], Strings(project.GetProperty("repos")));
    }

    [Fact]
    public async Task Save_ExistingId_ToAnotherProjectsFolder_IsRefused()
    {
        var other = FakeRepo(_h.Temp.Path, "outro");
        WriteConfig($$"""
            { "gitProjects": [
              { "id": "p1", "name": "A", "folder": {{JsonSerializer.Serialize(Workspace)}}, "repos": [] },
              { "id": "p2", "name": "B", "folder": {{JsonSerializer.Serialize(other)}}, "repos": [] } ] }
            """);

        var error = Error(await SendAsync("saveGitProject", new { project = new { id = "p1", name = "A", folder = other } }));

        Assert.Equal("Já existe outro projeto salvo com essa pasta.", error);
    }

    [Fact]
    public async Task Save_UnknownId_IsRefused()
    {
        SeeRepoInProcess(App);

        var error = Error(await SendAsync("saveGitProject", new { project = new { id = "nao-existe", name = "x", folder = Workspace } }));

        Assert.Equal("Projeto não encontrado.", error);
    }

    [Fact]
    public async Task Save_FiftyProjects_RefusesTheFiftyFirst()
    {
        var projects = Enumerable.Range(0, 50).Select(i => new { id = $"p{i}", name = $"P{i}", folder = $@"C:\projetos\p{i}", repos = Array.Empty<string>() });
        WriteConfig(JsonSerializer.Serialize(new { gitProjects = projects }));
        SeeRepoInProcess(App);

        var error = Error(await SendAsync("saveGitProject", new { project = new { id = "", name = "x", folder = Workspace } }));

        Assert.Equal("Limite de 50 projetos salvos atingido. Apague um para salvar outro.", error);
        Assert.Equal(50, _h.Engine.Config.GitProjects.Count);
    }

    [Fact]
    public async Task Save_InReadOnlyMode_IsRefused()
    {
        WriteConfig("{}");
        using var lockFile = new FileStream(_h.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var engine = _h.Engine; // carrega com o arquivo travado: somente leitura
        Assert.NotNull(engine.ConfigLoadError);
        SeeRepoInProcess(App);

        var error = Error(await SendAsync("saveGitProject", new { project = new { id = "", name = "x", folder = Workspace } }));

        Assert.StartsWith("A configuração não pôde ser lida", error, StringComparison.Ordinal);
    }

    // ---------- deleteGitProject ----------

    [Fact]
    public async Task Delete_RemovesProject_AndClearsTabReference()
    {
        SeeRepoInProcess(App);
        var id = Ok(await SendAsync("saveGitProject", new { project = new { id = "", name = "A", folder = Workspace } })).GetProperty("project").GetProperty("id").GetString();
        Ok(await SendAsync("setGitWorkspace", new { workspace = new { docked = true, dockWidthPct = 42, tabs = new[] { new { folder = Workspace, name = "A", repos = new[] { App }, projectId = id, activeRepo = App } }, active = Workspace } }));

        Ok(await SendAsync("deleteGitProject", new { id }));

        Assert.Empty(_h.Engine.Config.GitProjects);
        Assert.Null(Assert.Single(_h.Engine.Config.GitWorkspace.Tabs).ProjectId);
        Assert.Equal("Projeto não encontrado.", Error(await SendAsync("deleteGitProject", new { id })));
    }

    // ---------- pins ----------

    [Fact]
    public async Task SavedProjectRepos_StayKnownAfterExpiry_AndUnpinAfterDelete()
    {
        SeeRepoInProcess(App);
        var id = Ok(await SendAsync("saveGitProject", new { project = new { id = "", name = "A", folder = Workspace } })).GetProperty("project").GetProperty("id").GetString();
        _h.Source.Processes.Clear();
        _h.Engine.Sample();
        _h.Time.Advance(TimeSpan.FromHours(2));

        Assert.True(_h.Engine.KnownRepos.Contains(App));
        Assert.True(_h.Engine.KnownRepos.Contains(Api)); // nunca visto num processo: fixado pelo projeto

        Ok(await SendAsync("deleteGitProject", new { id }));

        Assert.False(_h.Engine.KnownRepos.Contains(Api));
    }

    [Fact]
    public void ConfigLoad_PinsOnlyLocalRealRepoRoots()
    {
        var notRepo = _h.Temp.Combine("sem-git");
        Directory.CreateDirectory(notRepo);
        var linked = _h.Temp.Combine("ligado");
        GitConfigSafetyTests.Junction(linked, App);
        var subfolder = Directory.CreateDirectory(Path.Combine(App, "src")).FullName;
        var repos = new[] { App, @"\\servidor\share\repo", notRepo, linked, subfolder, @"\\?\" + Api };
        WriteConfig(JsonSerializer.Serialize(new
        {
            gitWorkspace = new { tabs = new[] { new { folder = Workspace, name = "w", repos } } },
        }));

        var known = _h.Engine.KnownRepos;

        Assert.True(known.Contains(App));
        Assert.False(known.Contains(notRepo));
        Assert.False(known.Contains(linked));
        Assert.False(known.Contains(subfolder));
        Assert.False(known.Contains(Api));
    }

    [Fact]
    public async Task GitDetails_WorksForPinnedRepoWithoutAnyProcess()
    {
        if (_h.GitDetails is null)
        {
            return; // sem git instalado não há visão Git
        }

        using var real = GitTestRepo.CreateWithCommit();
        WriteConfig(JsonSerializer.Serialize(new
        {
            gitProjects = new[] { new { id = "p1", name = "r", folder = real.Path, repos = new[] { real.Path } } },
        }));

        var reply = await SendAsync("gitDetails", new { repoRoot = real.Path });

        Assert.Equal("inicial", Ok(reply).GetProperty("details").GetProperty("commits")[0].GetProperty("subject").GetString());
    }

    // ---------- ready / rescan ----------

    [Fact]
    public async Task Ready_OldConfigWithoutNewFields_ReturnsDefaults()
    {
        WriteConfig("""{ "favorites": [], "saved": [], "settings": { "refreshIntervalMs": 2000, "minimizeToTray": true, "theme": "dark" } }""");

        var config = Ok(await SendAsync("ready", null)).GetProperty("config");

        Assert.Equal(0, config.GetProperty("gitProjects").GetArrayLength());
        var ws = config.GetProperty("gitWorkspace");
        Assert.False(ws.GetProperty("docked").GetBoolean());
        Assert.Equal(42, ws.GetProperty("dockWidthPct").GetDouble());
        Assert.Equal(0, ws.GetProperty("tabs").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, ws.GetProperty("active").ValueKind);
        Assert.Equal("dark", config.GetProperty("settings").GetProperty("theme").GetString());
    }

    [Fact]
    public async Task Ready_RescansSavedProjectFolders_NewCloneAppears_DeletedRepoDisappears()
    {
        WriteConfig(JsonSerializer.Serialize(new
        {
            gitProjects = new[] { new { id = "p1", name = "g", folder = Workspace, repos = new[] { App, Api } } },
        }));
        _ = _h.Engine;
        var mobile = FakeRepo(Workspace, "mobile");
        Directory.Delete(Api, recursive: true);

        var project = Ok(await SendAsync("ready", null)).GetProperty("config").GetProperty("gitProjects")[0];

        Assert.Equal([App, mobile], Strings(project.GetProperty("repos")));
        Assert.Equal([App, mobile], _h.Engine.Config.GitProjects[0].Repos);
        Assert.True(_h.Engine.KnownRepos.Contains(mobile));
        Assert.False(_h.Engine.KnownRepos.Contains(Api));
    }

    // ---------- setGitWorkspace ----------

    [Fact]
    public async Task SetWorkspace_SanitizesAndPersists()
    {
        SeeRepoInProcess(App);

        var ws = Ok(await SendAsync("setGitWorkspace", new
        {
            workspace = new
            {
                docked = true,
                dockWidthPct = 95,
                tabs = new object[]
                {
                    new { folder = Workspace, name = "g", repos = new[] { App, Api, @"C:\Windows" }, projectId = "fantasma", activeRepo = @"C:\Windows" },
                    new { folder = @"C:\Windows", name = "w", repos = new[] { @"C:\Windows" }, projectId = (string?)null, activeRepo = (string?)null },
                },
                active = @"C:\Windows",
            },
        })).GetProperty("workspace");

        Assert.True(ws.GetProperty("docked").GetBoolean());
        Assert.Equal(70, ws.GetProperty("dockWidthPct").GetDouble());
        var tab = Assert.Single(ws.GetProperty("tabs").EnumerateArray());
        Assert.Equal([App], Strings(tab.GetProperty("repos"))); // Api não é conhecido
        Assert.Equal(JsonValueKind.Null, tab.GetProperty("projectId").ValueKind);
        Assert.Equal(App, tab.GetProperty("activeRepo").GetString());
        Assert.Equal(Workspace, ws.GetProperty("active").GetString());
        Assert.Equal(70, _h.Engine.Config.GitWorkspace.DockWidthPct);
    }

    [Fact]
    public async Task SetWorkspace_TabReposStayPinned()
    {
        SeeRepoInProcess(App);
        Ok(await SendAsync("setGitWorkspace", new { workspace = new { tabs = new[] { new { folder = App, name = "app", repos = new[] { App } } } } }));
        _h.Source.Processes.Clear();
        _h.Engine.Sample();

        _h.Time.Advance(TimeSpan.FromHours(3));

        Assert.True(_h.Engine.KnownRepos.Contains(App));
    }

    [Fact]
    public async Task SetWorkspace_InvalidPayloadType_IsRejected()
    {
        var reply = await SendAsync("setGitWorkspace", new { workspace = new { dockWidthPct = "largo" } });

        Assert.False(reply.GetProperty("ok").GetBoolean());
    }

    // ---------- projectScripts ----------

    [Fact]
    public async Task ProjectScripts_ListsScriptNamesPerRepo()
    {
        File.WriteAllText(Path.Combine(App, "package.json"), """{ "scripts": { "build": "ng build", "start": "ng serve" } }""");
        File.WriteAllText(Path.Combine(App, "package-lock.json"), "{}");
        File.WriteAllText(Path.Combine(Api, "package.json"), """{ "packageManager": "pnpm@9.0.0", "scripts": { "start:dev": "nest start --watch" } }""");
        SeeRepoInProcess(App);

        var repos = Ok(await SendAsync("projectScripts", new { folder = Workspace })).GetProperty("repos").EnumerateArray().ToList();

        Assert.Equal(2, repos.Count);
        Assert.Equal(Api, repos[0].GetProperty("repoRoot").GetString());
        Assert.Equal("pnpm", repos[0].GetProperty("packageManager").GetString());
        Assert.Equal(["start:dev"], Strings(repos[0].GetProperty("scripts")));
        Assert.Equal("npm", repos[1].GetProperty("packageManager").GetString());
        Assert.Equal(["start", "build"], Strings(repos[1].GetProperty("scripts")));
        Assert.Empty(_h.Starter.Launched);
    }

    [Fact]
    public async Task ProjectScripts_UnknownFolder_IsRefused()
    {
        Assert.Equal(UnknownFolderError, Error(await SendAsync("projectScripts", new { folder = Workspace })));
        Assert.Equal(UnknownFolderError, Error(await SendAsync("projectScripts", new { folder = @"\\servidor\share" })));
    }

    // ---------- rodada de correções (revisão de segurança + QA) ----------

    private const string BroadFolderError =
        "Pasta ampla demais para virar projeto (raiz da unidade, pasta do usuário ou do sistema). Escolha a pasta do projeto ou de um repositório.";

    [Fact]
    public async Task Draft_DoesNotRunGitStatusForReposNotYetKnown()
    {
        _h.AddProcess(100, "code.exe", 1000, "code .", Workspace);
        _h.Engine.Sample();
        var resolved = new List<string?>();
        _h.Git.OnResolve = dir => resolved.Add(dir);

        var repos = Ok(await SendAsync("gitProjectDraft", new { path = Workspace })).GetProperty("repos").EnumerateArray().ToList();

        Assert.Empty(resolved);
        Assert.All(repos, r =>
        {
            Assert.Equal("main", r.GetProperty("branch").GetString()); // lida do HEAD, sem git
            Assert.Equal(JsonValueKind.Null, r.GetProperty("dirtyCount").ValueKind);
        });
    }

    [Fact]
    public async Task Draft_KnownRepo_UsesSnapshotCache()
    {
        SeeRepoInProcess(App);
        var resolved = new List<string?>();
        _h.Git.OnResolve = dir => resolved.Add(dir);

        Ok(await SendAsync("gitProjectDraft", new { path = App }));

        Assert.Equal([App], resolved); // Api ainda não é conhecido: não consulta
    }

    public static TheoryData<string> BroadFolders() => new()
    {
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        @"C:\",
    };

    [Theory]
    [MemberData(nameof(BroadFolders))]
    public async Task BroadFolder_IsRefusedEvenWhenItIsAProcessWorkingDirectory(string broad)
    {
        _h.AddProcess(100, "cmd.exe", 1000, "cmd", broad);
        _h.Engine.Sample();

        Assert.Equal(BroadFolderError, Error(await SendAsync("gitProjectDraft", new { path = broad })));
        Assert.Equal(BroadFolderError, Error(await SendAsync("saveGitProject", new { project = new { id = "", name = "x", folder = broad } })));
        Assert.Equal(BroadFolderError, Error(await SendAsync("projectScripts", new { folder = broad })));
    }

    [Fact]
    public async Task Draft_ParentWithFiveOrMoreRepos_SuggestsTheRepoItself()
    {
        FakeRepo(Workspace, "mobile");
        FakeRepo(Workspace, "crm");
        FakeRepo(Workspace, "vida");
        SeeRepoInProcess(App);

        var project = Ok(await SendAsync("gitProjectDraft", new { path = App })).GetProperty("project");

        Assert.Equal(App, project.GetProperty("folder").GetString());
        Assert.Equal([App], Strings(project.GetProperty("repos")));
    }

    [Fact]
    public async Task Draft_ParentWithFourRepos_StillSuggestsParent()
    {
        FakeRepo(Workspace, "mobile");
        FakeRepo(Workspace, "crm");
        SeeRepoInProcess(App);

        var project = Ok(await SendAsync("gitProjectDraft", new { path = App })).GetProperty("project");

        Assert.Equal(Workspace, project.GetProperty("folder").GetString());
    }

    [Fact]
    public async Task Draft_FolderOfSavedProject_ReturnsSavedIdAndName()
    {
        SeeRepoInProcess(App);
        var id = Ok(await SendAsync("saveGitProject", new { project = new { id = "", name = "Integra One", folder = Workspace } }))
            .GetProperty("project").GetProperty("id").GetString();

        var project = Ok(await SendAsync("gitProjectDraft", new { path = App })).GetProperty("project");

        Assert.Equal(id, project.GetProperty("id").GetString());
        Assert.Equal("Integra One", project.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Save_NewWithExistingFolderAndDefaultName_KeepsCurrentName()
    {
        SeeRepoInProcess(App);
        Ok(await SendAsync("saveGitProject", new { project = new { id = "", name = "Integra One", folder = Workspace } }));

        var project = Ok(await SendAsync("saveGitProject", new { project = new { id = "", name = "gsintegra", folder = Workspace } })).GetProperty("project");

        Assert.Equal("Integra One", project.GetProperty("name").GetString());
        Assert.Equal("Integra One", Assert.Single(_h.Engine.Config.GitProjects).Name);
    }

    [Fact]
    public async Task Pins_MatchFinalConfig_AfterConcurrentChanges()
    {
        var repos = Enumerable.Range(0, 8).Select(i => FakeRepo(_h.Temp.Path, $"c{i}")).ToList();
        foreach (var repo in repos)
        {
            _h.Engine.KnownRepos.Register(repo);
        }

        await Task.WhenAll(repos.Select(repo => SendAsync("setGitWorkspace", new { workspace = new { tabs = new[] { new { folder = repo, name = "t", repos = new[] { repo } } } } })));
        _h.Time.Advance(TimeSpan.FromHours(1)); // os registrados expiram: sobra só o fixado

        var finalRepo = Assert.Single(_h.Engine.Config.GitWorkspace.Tabs).Repos.Single();
        Assert.All(repos, r => Assert.Equal(string.Equals(r, finalRepo, StringComparison.OrdinalIgnoreCase), _h.Engine.KnownRepos.Contains(r)));
    }

    [Fact]
    public async Task SetWorkspace_KeepsReposAlreadySavedInTabs_EvenIfNotPinnableNow()
    {
        var gone = FakeRepo(Workspace, "removido");
        WriteConfig(JsonSerializer.Serialize(new
        {
            gitWorkspace = new { tabs = new[] { new { folder = Workspace, name = "g", repos = new[] { App, gone } } } },
        }));
        _ = _h.Engine;
        Directory.Delete(gone, recursive: true);
        _h.Engine.GitProjects.RefreshPins();

        var tab = Ok(await SendAsync("setGitWorkspace", new { workspace = new { tabs = new[] { new { folder = Workspace, name = "g", repos = new[] { App, gone } } } } }))
            .GetProperty("workspace").GetProperty("tabs")[0];

        Assert.Equal([App, gone], Strings(tab.GetProperty("repos")));
        Assert.True(_h.Engine.KnownRepos.Contains(App));
        Assert.False(_h.Engine.KnownRepos.Contains(gone));
    }

    [Fact]
    public async Task SetWorkspace_SameValue_DoesNotRewriteConfig()
    {
        SeeRepoInProcess(App);
        var workspace = new { docked = true, dockWidthPct = 40, tabs = new[] { new { folder = App, name = "app", repos = new[] { App } } }, active = App };
        Ok(await SendAsync("setGitWorkspace", new { workspace }));
        var generation = _h.Engine.ConfigService.Generation;

        Ok(await SendAsync("setGitWorkspace", new { workspace }));

        Assert.Equal(generation, _h.Engine.ConfigService.Generation);
    }

    [Fact]
    public async Task Ready_RescansAtMostEveryThirtySeconds()
    {
        WriteConfig(JsonSerializer.Serialize(new
        {
            gitProjects = new[] { new { id = "p1", name = "g", folder = Workspace, repos = new[] { Api, App } } },
        }));
        Ok(await SendAsync("ready", null));
        var mobile = FakeRepo(Workspace, "mobile");

        Ok(await SendAsync("ready", null));
        Assert.DoesNotContain(mobile, _h.Engine.Config.GitProjects[0].Repos);

        _h.Time.Advance(TimeSpan.FromSeconds(31));
        Ok(await SendAsync("ready", null));
        Assert.Contains(mobile, _h.Engine.Config.GitProjects[0].Repos);
    }

    [Fact]
    public async Task LowercaseDrive_ComesBackUppercase_InDraftAndSave()
    {
        SeeRepoInProcess(App);
        var lowerApp = char.ToLowerInvariant(App[0]) + App[1..];

        var draft = Ok(await SendAsync("gitProjectDraft", new { path = lowerApp })).GetProperty("project");
        var saved = Ok(await SendAsync("saveGitProject", new { project = new { id = "", name = "g", folder = char.ToLowerInvariant(Workspace[0]) + Workspace[1..] } })).GetProperty("project");

        Assert.Equal(Workspace, draft.GetProperty("folder").GetString());
        Assert.Equal(Workspace, saved.GetProperty("folder").GetString());
        Assert.Equal([Api, App], Strings(saved.GetProperty("repos")));
    }
}
