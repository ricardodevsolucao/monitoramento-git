using Monitoramento.Core.Actions;
using Monitoramento.Core.Git;
using Monitoramento.Core.Models;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Git;

/// <summary>Ações git (estilo Source Control do VS Code), sempre em repositórios temporários.</summary>
public sealed class GitActionServiceTests : IDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly GitTestRepo _repo = GitTestRepo.CreateEmpty();
    private readonly CountingGitRunner _runner;
    private readonly GitDetailsService _details;
    private readonly GitActionService _actions;

    public GitActionServiceTests()
    {
        _runner = CountingGitRunner.Real();
        var known = new KnownGitRepos(_time);
        _details = new GitDetailsService(_runner, known, _time);
        _actions = new GitActionService(_details, _runner);
        _repo.CommitFile("a.txt", "a", "inicial");
        known.Register(_repo.Path);
    }

    public void Dispose() => _repo.Dispose();

    private string Root => _repo.Path;

    private static GitActionResult Ok(OperationResult<GitActionResult> result)
    {
        Assert.True(result.Ok, result.Error);
        return result.Value!;
    }

    private static string Fail(OperationResult<GitActionResult> result)
    {
        Assert.False(result.Ok);
        return result.Error!;
    }

    /// <summary>Clona o bare como "origin", com identidade e sem conversão de fim de linha.</summary>
    private string CloneRemote(string bare, string name)
    {
        var path = _repo.Temp.Combine(name);
        GitTestRepo.RunGit(_repo.Temp.Path, "clone", "-q", bare, path);
        GitTestRepo.RunGit(path, "config", "user.name", "Outro");
        GitTestRepo.RunGit(path, "config", "user.email", "outro@exemplo.com");
        GitTestRepo.RunGit(path, "config", "core.autocrlf", "false");
        return path;
    }

    private string AddOriginAndPush()
    {
        var bare = _repo.CreateBareRemote();
        _repo.Git("remote", "add", "origin", bare);
        _repo.Git("push", "-q", "-u", "origin", "main");
        return bare;
    }

    [Fact]
    public async Task Stage_SpecificPathsAndAll()
    {
        _repo.WriteFile("a.txt", "alterado");
        _repo.WriteFile("novo.txt", "n");

        var one = Ok(await _actions.StageAsync(Root, ["novo.txt"]));
        Assert.Equal(1, one.Details.StagedCount);
        Assert.Equal(1, one.Details.UnstagedCount);

        var all = Ok(await _actions.StageAsync(Root, []));
        Assert.Equal(2, all.Details.StagedCount);
        Assert.Equal(0, all.Details.UnstagedCount);
    }

    [Fact]
    public async Task Unstage_SpecificAndAll()
    {
        _repo.WriteFile("a.txt", "alterado");
        _repo.WriteFile("novo.txt", "n");
        _repo.Git("add", "-A");

        var one = Ok(await _actions.UnstageAsync(Root, ["a.txt"]));
        Assert.Contains(new GitChange("a.txt", null, null, GitWorktreeState.M, false), one.Details.Changes!);

        var all = Ok(await _actions.UnstageAsync(Root, []));
        Assert.Equal(0, all.Details.StagedCount);
        Assert.Equal(1, all.Details.UntrackedCount);
    }

    [Fact]
    public async Task Discard_TrackedRestoresAndUntrackedIsDeleted()
    {
        _repo.WriteFile("a.txt", "alterado");
        _repo.WriteFile("lixo.txt", "x");

        var result = Ok(await _actions.DiscardAsync(Root, ["a.txt", "lixo.txt"]));

        Assert.Equal("a", File.ReadAllText(_repo.FullPath("a.txt")));
        Assert.False(File.Exists(_repo.FullPath("lixo.txt")));
        Assert.Empty(result.Details.Changes!);
    }

    [Theory]
    [InlineData("../fora.txt")]
    [InlineData("pasta/../../fora.txt")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData(":(glob)*")]
    [InlineData("nao-mudou.txt")]
    [InlineData("")]
    public async Task PathActions_RefuseInvalidOrUnchangedPathsWithoutRunningAction(string path)
    {
        _repo.WriteFile("a.txt", "alterado");
        _repo.Git("add", "-A");
        var staged = _repo.Git("diff", "--cached", "--name-only");

        Fail(await _actions.StageAsync(Root, [path]));
        Fail(await _actions.UnstageAsync(Root, [path]));
        Fail(await _actions.DiscardAsync(Root, [path]));

        Assert.Equal(staged, _repo.Git("diff", "--cached", "--name-only"));
        Assert.Equal("alterado", File.ReadAllText(_repo.FullPath("a.txt")));
        Assert.DoesNotContain(_runner.Commands, c => c is "add" or "restore" or "clean" or "--literal-pathspecs");
    }

    [Fact]
    public async Task Discard_PathThroughJunction_IsRefused()
    {
        var outside = _repo.Temp.Combine("fora");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "alvo.txt"), "seguro");
        GitConfigSafetyTests.Junction(_repo.FullPath("lnk"), outside);
        var untracked = _repo.Git("status", "--porcelain").Split('\n').Select(l => l.Trim()[3..]).ToList();
        Assert.NotEmpty(untracked);

        foreach (var path in untracked)
        {
            var error = Fail(await _actions.DiscardAsync(Root, [path]));
            Assert.Contains("link", error, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("seguro", File.ReadAllText(Path.Combine(outside, "alvo.txt")));
        Assert.True(Directory.Exists(_repo.FullPath("lnk")));
    }

    [Fact]
    public async Task RefNames_ArePassedAfterEndOfOptions()
    {
        _repo.Git("switch", "-q", "-c", "feat");
        _repo.CommitFile("f.txt", "f", "feat");
        _repo.Git("switch", "-q", "main");
        _repo.CommitFile("m.txt", "m", "main");

        Ok(await _actions.MergeAsync(Root, "feat"));
        Ok(await _actions.CheckoutAsync(Root, "nova", create: true, startPoint: "main"));
        Ok(await _actions.CheckoutAsync(Root, "feat", create: false, startPoint: null));

        AssertAfterEndOfOptions("merge", "feat");
        AssertAfterEndOfOptions("switch", "main");
        AssertAfterEndOfOptions("switch", "feat");
    }

    private void AssertAfterEndOfOptions(string command, string refName)
    {
        var request = _runner.Requests.Last(r => r[0] == command && r[^1] == refName);
        Assert.Equal("--end-of-options", request[^2]);
    }

    [Fact]
    public async Task Commit_EmptyMessage_FailsWithoutCallingGit()
    {
        _repo.WriteFile("a.txt", "x");
        _repo.Git("add", "-A");

        var error = Fail(await _actions.CommitAsync(Root, "   \n ", all: false, amend: false, push: false));

        Assert.Contains("mensagem", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _runner.Calls);
    }

    [Fact]
    public async Task Commit_MessageGoesThroughStdinVerbatim()
    {
        _repo.WriteFile("a.txt", "x");
        _repo.Git("add", "-A");
        const string message = "feat: ação \"especial\" -m --amend $(rm) `x` & | <> %PATH%\n\ncorpo";

        var result = Ok(await _actions.CommitAsync(Root, message, all: false, amend: false, push: false));

        Assert.Equal(message, _repo.Git("log", "-1", "--format=%B").Trim());
        Assert.Equal("feat: ação \"especial\" -m --amend $(rm) `x` & | <> %PATH%", result.Details.Commits[0].Subject);
    }

    [Fact]
    public async Task Commit_AllStagesEverythingIncludingUntracked()
    {
        _repo.WriteFile("a.txt", "x");
        _repo.WriteFile("novo.txt", "n");

        var result = Ok(await _actions.CommitAsync(Root, "tudo", all: true, amend: false, push: false));

        Assert.Empty(result.Details.Changes!);
        Assert.Equal("tudo", result.Details.Commits[0].Subject);
    }

    [Fact]
    public async Task Commit_AmendReplacesLastCommit()
    {
        var before = _repo.Git("rev-list", "--count", "HEAD");

        var result = Ok(await _actions.CommitAsync(Root, "inicial corrigido", all: false, amend: true, push: false));

        Assert.Equal(before, _repo.Git("rev-list", "--count", "HEAD"));
        Assert.Equal("inicial corrigido", result.Details.Commits[0].Subject);
    }

    [Fact]
    public async Task Commit_FailingPreCommitHook_ReturnsHookOutput()
    {
        File.WriteAllText(Path.Combine(Root, ".git", "hooks", "pre-commit"), "#!/bin/sh\necho 'lint falhou: 3 erros' >&2\nexit 1\n");
        _repo.WriteFile("a.txt", "x");
        _repo.Git("add", "-A");

        var error = Fail(await _actions.CommitAsync(Root, "nao vai", all: false, amend: false, push: false));

        Assert.StartsWith("Falha ao fazer commit:", error, StringComparison.Ordinal);
        Assert.Contains("lint falhou: 3 erros", error, StringComparison.Ordinal);
        Assert.Equal("inicial", _repo.Git("log", "-1", "--format=%s"));
    }

    [Fact]
    public async Task Checkout_CreateSwitchAndStartPoint()
    {
        var first = _repo.Git("rev-parse", "HEAD");
        _repo.CommitFile("b.txt", "b", "segundo");

        var created = Ok(await _actions.CheckoutAsync(Root, "feat/nova", create: true, startPoint: null));
        Assert.Equal("feat/nova", created.Details.Head.Branch);

        var fromStart = Ok(await _actions.CheckoutAsync(Root, "antiga", create: true, startPoint: "main"));
        Assert.Equal("antiga", fromStart.Details.Head.Branch);

        var back = Ok(await _actions.CheckoutAsync(Root, "main", create: false, startPoint: null));
        Assert.Equal("main", back.Details.Head.Branch);
        Assert.NotEqual(first, back.Details.Head.Sha);
    }

    [Theory]
    [InlineData("nome com espaço")]
    [InlineData("-x")]
    [InlineData("a..b")]
    [InlineData("@{-1}")]
    [InlineData("x.lock")]
    [InlineData("")]
    public async Task Checkout_InvalidNewBranchName_IsRefused(string name)
    {
        var error = Fail(await _actions.CheckoutAsync(Root, name, create: true, startPoint: null));

        Assert.Contains("branch", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("switch", _runner.Commands);
    }

    [Fact]
    public async Task Checkout_UnknownBranchOrStartPoint_IsRefused()
    {
        Fail(await _actions.CheckoutAsync(Root, "nao-existe", create: false, startPoint: null));
        Fail(await _actions.CheckoutAsync(Root, "nova", create: true, startPoint: "nao-existe"));
        Assert.DoesNotContain("switch", _runner.Commands);
    }

    [Fact]
    public async Task Checkout_RemoteBranchWithoutLocal_TracksIt()
    {
        var bare = AddOriginAndPush();
        var other = CloneRemote(bare, "outro");
        GitTestRepo.RunGit(other, "switch", "-q", "-c", "remota");
        File.WriteAllText(Path.Combine(other, "r.txt"), "r");
        GitTestRepo.RunGit(other, "add", "-A");
        GitTestRepo.RunGit(other, "commit", "-q", "-m", "na remota");
        GitTestRepo.RunGit(other, "push", "-q", "-u", "origin", "remota");
        Ok(await _actions.FetchAsync(Root));

        var result = Ok(await _actions.CheckoutAsync(Root, "origin/remota", create: false, startPoint: null));

        Assert.Equal("remota", result.Details.Head.Branch);
        Assert.Equal("origin/remota", result.Details.Head.Upstream);
    }

    [Fact]
    public async Task Checkout_WithConflictingLocalChanges_ReturnsGitMessage()
    {
        _repo.Git("switch", "-q", "-c", "outra");
        _repo.CommitFile("a.txt", "na outra", "outra");
        _repo.Git("switch", "-q", "main");
        _repo.WriteFile("a.txt", "local");

        var error = Fail(await _actions.CheckoutAsync(Root, "outra", create: false, startPoint: null));

        Assert.StartsWith("Falha ao trocar de branch:", error, StringComparison.Ordinal);
        Assert.True(error.Length < 1200);
    }

    [Fact]
    public async Task Merge_Clean()
    {
        _repo.Git("switch", "-q", "-c", "feat");
        _repo.CommitFile("f.txt", "f", "feat");
        _repo.Git("switch", "-q", "main");
        _repo.CommitFile("m.txt", "m", "main");

        var result = Ok(await _actions.MergeAsync(Root, "feat"));

        Assert.False(result.Details.Merging);
        Assert.Equal(2, result.Details.Commits[0].Parents.Count);
    }

    [Fact]
    public async Task Merge_ConflictIsOkWithMergingThenAbort()
    {
        _repo.Git("switch", "-q", "-c", "feat");
        _repo.CommitFile("a.txt", "feat", "feat");
        _repo.Git("switch", "-q", "main");
        _repo.CommitFile("a.txt", "main", "main");

        var merged = Ok(await _actions.MergeAsync(Root, "feat"));
        Assert.True(merged.Details.Merging);
        Assert.Equal(1, merged.Details.ConflictCount);
        Assert.Contains(merged.Details.Changes!, c => c.Path == "a.txt" && c.Conflict);

        var aborted = Ok(await _actions.MergeAbortAsync(Root));
        Assert.False(aborted.Details.Merging);
        Assert.Empty(aborted.Details.Changes!);
        Assert.Equal("main", File.ReadAllText(_repo.FullPath("a.txt")));
    }

    [Fact]
    public async Task Merge_UnknownBranch_IsRefused()
    {
        Fail(await _actions.MergeAsync(Root, "nao-existe"));
        Assert.DoesNotContain("merge", _runner.Commands);
    }

    [Fact]
    public async Task Push_WithoutUpstreamAndOneRemote_SetsUpstream()
    {
        var bare = _repo.CreateBareRemote();
        _repo.Git("remote", "add", "origin", bare);

        var result = Ok(await _actions.PushAsync(Root));

        Assert.Equal("origin/main", result.Details.Head.Upstream);
        Assert.Equal(["origin"], result.Details.Remotes);
        Assert.Equal(_repo.Git("rev-parse", "HEAD"), GitTestRepo.RunGit(bare, "rev-parse", "main"));
    }

    [Fact]
    public async Task Push_WithoutRemote_FailsInPortuguese()
    {
        var error = Fail(await _actions.PushAsync(Root));

        Assert.Contains("remoto", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CommitAndPush_PushesToUpstream()
    {
        var bare = AddOriginAndPush();
        _repo.WriteFile("a.txt", "x");

        var result = Ok(await _actions.CommitAsync(Root, "e push", all: true, amend: false, push: true));

        Assert.Equal(_repo.Git("rev-parse", "HEAD"), GitTestRepo.RunGit(bare, "rev-parse", "main"));
        Assert.Equal(0, result.Details.Repo.Ahead);
    }

    [Fact]
    public async Task FetchAndPull_FastForward()
    {
        var bare = AddOriginAndPush();
        var other = CloneRemote(bare, "outro");
        File.WriteAllText(Path.Combine(other, "o.txt"), "o");
        GitTestRepo.RunGit(other, "add", "-A");
        GitTestRepo.RunGit(other, "commit", "-q", "-m", "do outro");
        GitTestRepo.RunGit(other, "push", "-q");

        var fetched = Ok(await _actions.FetchAsync(Root));
        Assert.Equal(1, fetched.Details.Repo.Behind);

        var pulled = Ok(await _actions.PullAsync(Root));
        Assert.Equal("do outro", pulled.Details.Commits[0].Subject);
        Assert.True(File.Exists(_repo.FullPath("o.txt")));
    }

    [Fact]
    public async Task Pull_DivergedBranch_FailsAskingForManualMerge()
    {
        var bare = AddOriginAndPush();
        var other = CloneRemote(bare, "outro");
        File.WriteAllText(Path.Combine(other, "o.txt"), "o");
        GitTestRepo.RunGit(other, "add", "-A");
        GitTestRepo.RunGit(other, "commit", "-q", "-m", "do outro");
        GitTestRepo.RunGit(other, "push", "-q");
        _repo.CommitFile("l.txt", "l", "local");

        var error = Fail(await _actions.PullAsync(Root));

        Assert.Contains("merge", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("local", _repo.Git("log", "-1", "--format=%s"));
    }

    [Fact]
    public async Task SecondConcurrentActionOnSameRepo_IsRefused()
    {
        using var slow = new SlowActions();
        slow.Repo.WriteFile("a.txt", "x");

        var first = slow.Actions.StageAsync(slow.Repo.Path, []);
        await Task.Delay(50);
        var second = await slow.Actions.FetchAsync(slow.Repo.Path);

        Assert.Equal(GitActionService.BusyError, second.Error);
        Assert.True((await first).Ok);
    }

    [Fact]
    public async Task UnknownRepo_IsRefused()
    {
        using var other = GitTestRepo.CreateWithCommit();

        Assert.Equal(GitDetailsService.UnknownRepoError, Fail(await _actions.FetchAsync(other.Path)));
    }

    [Fact]
    public void Timeouts_AreSixtySecondsLocalAndThreeMinutesNetwork()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), GitActionService.LocalTimeout);
        Assert.Equal(TimeSpan.FromSeconds(180), GitActionService.NetworkTimeout);
    }

    /// <summary>Ações com cada execução do git atrasada (para segurar o lock do repositório).</summary>
    private sealed class SlowActions : IDisposable
    {
        public SlowActions()
        {
            var time = new ManualTimeProvider();
            var known = new KnownGitRepos(time);
            var runner = CountingGitRunner.Real(TimeSpan.FromMilliseconds(400));
            Actions = new GitActionService(new GitDetailsService(runner, known, time), runner);
            Repo.CommitFile("a.txt", "a", "inicial");
            known.Register(Repo.Path);
        }

        public GitTestRepo Repo { get; } = GitTestRepo.CreateEmpty();

        public GitActionService Actions { get; }

        public void Dispose() => Repo.Dispose();
    }
}
