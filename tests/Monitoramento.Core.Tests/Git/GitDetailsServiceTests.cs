using Monitoramento.Core.Git;
using Monitoramento.Core.Models;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Git;

/// <summary>gitDetails contra repositórios git reais em pastas temporárias.</summary>
public sealed class GitDetailsServiceTests
{
    private readonly ManualTimeProvider _time = new();

    private (GitDetailsService Service, KnownGitRepos Known, CountingGitRunner Runner) NewService(TimeSpan? delay = null)
    {
        var known = new KnownGitRepos(_time);
        var runner = CountingGitRunner.Real(delay);
        return (new GitDetailsService(runner, known, _time), known, runner);
    }

    private static GitTestRepo CreateRichRepo()
    {
        var repo = GitTestRepo.CreateEmpty();
        repo.WriteFile("a.txt", "a");
        repo.CommitFile("readme.txt", "ola", "inicial");
        repo.Git("switch", "-q", "-c", "feat");
        repo.CommitFile("feat.txt", "f", "feat: nova, coisa");
        repo.Git("switch", "-q", "main");
        repo.CommitFile("main.txt", "m", "main 2");
        repo.Git("merge", "-q", "--no-ff", "feat", "-m", "merge feat");
        repo.Git("tag", "v1");
        repo.WriteFile("staged.txt", "s");
        repo.Git("add", "staged.txt");
        repo.Git("mv", "a.txt", "b.txt");
        repo.WriteFile("u.txt", "u");
        return repo;
    }

    [Fact]
    public async Task RealRepo_ReturnsCompleteDetails()
    {
        using var repo = CreateRichRepo();
        var (service, known, _) = NewService();
        known.Register(repo.Path);

        var result = await service.GetAsync(repo.Path, null);

        Assert.True(result.Ok, result.Error);
        var d = result.Value!;
        Assert.Null(d.Error);
        Assert.False(d.Truncated);
        Assert.Equal("main", d.Head.Branch);
        Assert.False(d.Head.Detached);
        Assert.Equal(repo.Git("rev-parse", "HEAD"), d.Head.Sha);
        Assert.Null(d.Head.Upstream);
        Assert.Equal(["feat", "main"], d.Branches.Select(b => b.Name));
        Assert.True(d.Branches.Single(b => b.Name == "main").Current);
        Assert.Equal("main", d.Repo.Branch);
        Assert.Equal(3, d.Repo.DirtyCount);
        Assert.Null(d.Repo.StatusNote);

        Assert.NotNull(d.Changes);
        Assert.Contains(new GitChange("staged.txt", null, GitIndexState.A, null, false), d.Changes);
        Assert.Contains(new GitChange("b.txt", "a.txt", GitIndexState.R, null, false), d.Changes);
        Assert.Contains(new GitChange("u.txt", null, null, GitWorktreeState.Untracked, false), d.Changes);
        Assert.Equal(2, d.StagedCount);
        Assert.Equal(0, d.UnstagedCount);
        Assert.Equal(1, d.UntrackedCount);
        Assert.Equal(0, d.ConflictCount);
        Assert.False(d.Merging);
        Assert.False(d.Rebasing);
        Assert.Empty(d.Remotes);

        Assert.Equal(4, d.Commits.Count);
        var merge = d.Commits[0];
        Assert.Equal("merge feat", merge.Subject);
        Assert.Equal(2, merge.Parents.Count);
        Assert.Equal(merge.Sha[..7], merge.ShortSha);
        Assert.Equal("Teste", merge.Author);
        Assert.True(DateTimeOffset.TryParse(merge.Date, out _));
        Assert.Contains(new GitRef("main", GitRefKind.Head), merge.Refs);
        Assert.Contains(new GitRef("v1", GitRefKind.Tag), merge.Refs);
        Assert.Contains(merge.Edges, e => e.Kind == GitEdgeKind.Merge);
        Assert.Equal(2, d.Graph.Lanes);
        Assert.Equal("inicial", d.Commits[^1].Subject);
    }

    [Fact]
    public async Task MaxCommits_TruncatesAndClamps()
    {
        using var repo = GitTestRepo.CreateEmpty();
        for (var i = 0; i < 4; i++)
        {
            repo.CommitFile("f.txt", i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"c{i}");
        }

        var (service, known, _) = NewService();
        known.Register(repo.Path);

        var two = (await service.GetAsync(repo.Path, 2)).Value!;
        Assert.True(two.Truncated);
        Assert.Equal(2, two.Commits.Count);
        Assert.Equal(1, two.Graph.Lanes);
        Assert.Contains(two.Commits[^1].Edges, e => e.ToSha == repo.Git("rev-parse", "HEAD~2")); // pai fora da janela

        var all = (await service.GetAsync(repo.Path, 4)).Value!;
        Assert.False(all.Truncated);
        Assert.Equal(4, all.Commits.Count);

        Assert.Equal(1, GitDetailsService.ClampMaxCommits(0));
        Assert.Equal(1, GitDetailsService.ClampMaxCommits(-5));
        Assert.Equal(150, GitDetailsService.ClampMaxCommits(null));
        Assert.Equal(500, GitDetailsService.ClampMaxCommits(10_000));
        Assert.Equal(200, GitDetailsService.ClampMaxCommits(200));
    }

    [Fact]
    public async Task UnknownRepo_IsRefusedWithoutRunningGit()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        var (service, _, runner) = NewService();

        var result = await service.GetAsync(repo.Path, null);

        Assert.False(result.Ok);
        Assert.Equal(GitDetailsService.UnknownRepoError, result.Error);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task KnownRepo_MatchesIgnoringCaseAndTrailingSeparator_AndExpiresAfter30Minutes()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        var (service, known, _) = NewService();
        known.Register(repo.Path);

        Assert.True((await service.GetAsync(repo.Path.ToUpperInvariant() + "\\", null)).Ok);

        _time.Advance(TimeSpan.FromMinutes(31));
        var expired = await service.GetAsync(repo.Path, null);
        Assert.Equal(GitDetailsService.UnknownRepoError, expired.Error);
    }

    [Theory]
    [InlineData(@"\\servidor\share\repo")]
    [InlineData("//servidor/share/repo")]
    [InlineData(@"\\?\C:\repo")]
    [InlineData("repo-relativo")]
    [InlineData("")]
    [InlineData(null)]
    public async Task NonLocalPath_IsRefused(string? path)
    {
        var (service, known, runner) = NewService();
        known.Register(@"\\servidor\share\repo");

        var result = await service.GetAsync(path, null);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task ConcurrentCalls_ShareOneRead_AndCacheLastsFiveSeconds()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        var (service, known, runner) = NewService(TimeSpan.FromMilliseconds(200));
        known.Register(repo.Path);

        var results = await Task.WhenAll(service.GetAsync(repo.Path, null), service.GetAsync(repo.Path, null));
        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Same(results[0].Value, results[1].Value);
        Assert.Equal(1, runner.Commands.Count(c => c == "log"));

        await service.GetAsync(repo.Path, null);
        Assert.Equal(1, runner.Commands.Count(c => c == "log")); // ainda no cache

        _time.Advance(TimeSpan.FromSeconds(6));
        await service.GetAsync(repo.Path, null);
        Assert.Equal(2, runner.Commands.Count(c => c == "log"));
    }

    [Fact]
    public async Task ForcedRefresh_BypassesCache()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        var (service, known, runner) = NewService(TimeSpan.FromMilliseconds(200));
        known.Register(repo.Path);

        await service.GetAsync(repo.Path, null);
        await service.GetAsync(repo.Path, null, force: true);

        Assert.Equal(2, runner.Commands.Count(c => c == "log"));
    }

    [Fact]
    public async Task AtMostTwoReadsRunAtTheSameTime()
    {
        var repos = Enumerable.Range(0, 4).Select(_ => GitTestRepo.CreateWithCommit()).ToList();
        try
        {
            var known = new KnownGitRepos(_time);
            var probe = new ConcurrencyProbe(CountingGitRunner.Real());
            var service = new GitDetailsService(probe, known, _time);
            repos.ForEach(r => known.Register(r.Path));

            await Task.WhenAll(repos.Select(r => service.GetAsync(r.Path, null)));

            Assert.InRange(probe.MaxReposInFlight, 1, 2);
        }
        finally
        {
            repos.ForEach(r => r.Dispose());
        }
    }

    [Fact]
    public async Task EmptyRepo_HasNoCommitsAndNoError()
    {
        using var repo = GitTestRepo.CreateEmpty();
        repo.WriteFile("x.txt", "x");
        var (service, known, _) = NewService();
        known.Register(repo.Path);

        var d = (await service.GetAsync(repo.Path, null)).Value!;

        Assert.Empty(d.Commits);
        Assert.Null(d.Error);
        Assert.Equal("main", d.Head.Branch);
        Assert.Null(d.Head.Sha);
        Assert.Equal(1, d.UntrackedCount);
    }

    [Fact]
    public async Task DetachedHead_IsReported()
    {
        using var repo = GitTestRepo.CreateEmpty();
        var first = repo.CommitFile("a.txt", "1", "um");
        repo.CommitFile("a.txt", "2", "dois");
        repo.Git("switch", "-q", "--detach", first);
        var (service, known, _) = NewService();
        known.Register(repo.Path);

        var d = (await service.GetAsync(repo.Path, null)).Value!;

        Assert.True(d.Head.Detached);
        Assert.Null(d.Head.Branch);
        Assert.Equal(first, d.Head.Sha);
        Assert.Equal(first[..7], d.Repo.Branch);
        Assert.Contains(new GitRef("HEAD", GitRefKind.Head), d.Commits.Single(c => c.Sha == first).Refs);
    }

    [Theory]
    [InlineData("gpg.program")]
    [InlineData("log.showSignature")]
    [InlineData("diff.bin.textconv")]
    [InlineData("core.alternateRefsCommand")]
    public async Task UnsafeLogConfig_RunsNothingAndExplains(string key)
    {
        using var repo = GitTestRepo.CreateWithCommit();
        var marker = repo.Temp.Combine("marker");
        var script = WriteMarkerScript(repo, marker);
        repo.Git("config", key, key == "log.showSignature" ? "true" : script);
        var (service, known, runner) = NewService();
        known.Register(repo.Path);

        var d = (await service.GetAsync(repo.Path, null)).Value!;

        Assert.Equal(0, runner.Calls);
        Assert.False(File.Exists(marker));
        Assert.Empty(d.Commits);
        Assert.Null(d.Changes);
        Assert.NotNull(d.Error);
        Assert.StartsWith("Histórico não lido", d.Error, StringComparison.Ordinal);
        Assert.NotNull(d.Repo.StatusNote);
        Assert.Equal("main", d.Head.Branch);
    }

    [Fact]
    public async Task UnsafeStatusConfig_SkipsStatusButStillReadsLog()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        repo.Git("config", "filter.x.clean", "algo");
        var (service, known, runner) = NewService();
        known.Register(repo.Path);

        var d = (await service.GetAsync(repo.Path, null)).Value!;

        Assert.Null(d.Changes);
        Assert.Equal(GitRepoSafety.FilterNote, d.Repo.StatusNote);
        Assert.Null(d.Repo.DirtyCount);
        Assert.Single(d.Commits);
        Assert.Null(d.Error);
        Assert.DoesNotContain("status", runner.Commands);
    }

    [Fact]
    public async Task ReadRunner_OverridesSignatureVerificationEvenIfConfigAsksForIt()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        var marker = repo.Temp.Combine("marker");
        var script = WriteMarkerScript(repo, marker);
        CreateFakeSignedCommit(repo);
        repo.Git("config", "gpg.program", script);
        repo.Git("config", "log.showSignature", "true");

        // controle positivo: o git do terminal executa o "gpg" da config local
        GitTestRepo.RunGit(repo.Path, "log", "-1");
        Assert.True(File.Exists(marker));
        File.Delete(marker);

        var runner = CountingGitRunner.Real();
        var result = await runner.RunAsync(GitDetailsReader.LogRequest(repo.Path, 10));

        Assert.True(result.Ok);
        Assert.Contains("assinado", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void ReadStartInfo_HasHardenedOptionsBeforeTheCommand()
    {
        var psi = GitProcessRunner.BuildStartInfo(@"C:\git\git.exe", new GitRunRequest(@"C:\repo", ["log", "-n", "5"], GitRunMode.Read));

        var args = psi.ArgumentList.ToList();
        Assert.Equal(
            [
                "-c", "core.fsmonitor=false", "-c", "core.hooksPath=NUL", "-c", "core.untrackedCache=false",
                "-c", "core.pager=", "-c", "log.showSignature=false", "-c", "gpg.program=",
                "-c", "core.alternateRefsCommand=",
            ],
            args.Take(14));
        var command = args.IndexOf("log");
        Assert.Equal(["--no-optional-locks", "--no-pager", "-C", @"C:\repo"], args.Skip(command - 4).Take(4));
        Assert.Equal("0", psi.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("0", psi.Environment["GIT_OPTIONAL_LOCKS"]);
        Assert.False(psi.Environment.ContainsKey("GIT_DIR"));
        Assert.False(psi.UseShellExecute);
    }

    [Fact]
    public void ActionStartInfo_KeepsHooksButDisablesPagerAndPrompts()
    {
        var psi = GitProcessRunner.BuildStartInfo(@"C:\git\git.exe", new GitRunRequest(@"C:\repo", ["commit", "-F", "-"], GitRunMode.Action));

        var args = psi.ArgumentList.ToList();
        Assert.DoesNotContain("core.hooksPath=NUL", args);
        Assert.Contains("core.pager=", args);
        Assert.Contains("--no-pager", args);
        Assert.Equal("0", psi.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal(["-C", @"C:\repo", "commit", "-F", "-"], args.Skip(args.Count - 5));
    }

    [Fact]
    public async Task Runner_OutputOverLimitIsReportedAsOverflow()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        var runner = CountingGitRunner.Real();

        var result = await runner.RunAsync(new GitRunRequest(repo.Path, ["log", "--format=%H%n%H%n%H"], GitRunMode.Read, MaxOutputBytes: 16));

        Assert.True(result.Overflow);
        Assert.False(result.Ok);
        Assert.True(result.Output.Length <= 16);
    }

    [Fact]
    public async Task Runner_TimeoutKillsAndIsReported()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        var runner = CountingGitRunner.Real();

        var result = await runner.RunAsync(new GitRunRequest(repo.Path, ["log"], GitRunMode.Read, Timeout: TimeSpan.FromMilliseconds(1)));

        Assert.True(result.TimedOut);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Runner_MissingExecutableFailsWithoutThrowing()
    {
        var runner = new GitProcessRunner(@"C:\nao\existe\git.exe");

        var result = await runner.RunAsync(new GitRunRequest(@"C:\", ["status"], GitRunMode.Read));

        Assert.True(result.StartFailed);
        Assert.False(result.Ok);
    }

    /// <summary>Script sh (o git for Windows executa shebang) que cria o arquivo marcador.</summary>
    private static string WriteMarkerScript(GitTestRepo repo, string marker)
    {
        var script = repo.Temp.Combine("fakegpg");
        File.WriteAllText(script, $"#!/bin/sh\necho executou > \"{marker.Replace('\\', '/')}\"\nexit 1\n");
        return script.Replace('\\', '/');
    }

    /// <summary>Commit com cabeçalho gpgsig falso: faz o git chamar o gpg.program para verificar.</summary>
    private static void CreateFakeSignedCommit(GitTestRepo repo)
    {
        var tree = repo.Git("rev-parse", "HEAD^{tree}");
        var parent = repo.Git("rev-parse", "HEAD");
        var body = $"tree {tree}\nparent {parent}\nauthor T <t@x> 1700000000 +0000\ncommitter T <t@x> 1700000000 +0000\n"
            + "gpgsig -----BEGIN PGP SIGNATURE-----\n \n abc\n -----END PGP SIGNATURE-----\n\nassinado\n";
        var file = repo.Temp.Combine("commit.txt");
        File.WriteAllText(file, body);
        var sha = repo.Git("hash-object", "-t", "commit", "-w", file);
        repo.Git("update-ref", "refs/heads/main", sha);
    }

    /// <summary>Mede quantos repositórios diferentes têm git rodando ao mesmo tempo.</summary>
    private sealed class ConcurrencyProbe(IGitRunner inner) : IGitRunner
    {
        private readonly Dictionary<string, int> _running = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _gate = new();

        public int MaxReposInFlight { get; private set; }

        public async Task<GitRunResult> RunAsync(GitRunRequest request, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _running[request.RepoRoot] = _running.GetValueOrDefault(request.RepoRoot) + 1;
                MaxReposInFlight = Math.Max(MaxReposInFlight, _running.Count);
            }

            try
            {
                await Task.Delay(100, cancellationToken);
                return await inner.RunAsync(request, cancellationToken);
            }
            finally
            {
                lock (_gate)
                {
                    if (--_running[request.RepoRoot] == 0)
                    {
                        _running.Remove(request.RepoRoot);
                    }
                }
            }
        }
    }
}
