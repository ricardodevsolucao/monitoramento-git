using Monitoramento.Core.Git;
using Monitoramento.Core.Models;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Git;

/// <summary>S1: o git status automático não pode executar código vindo da config do repositório.</summary>
public sealed class GitSafetyTests
{
    private static GitStatusCache NewCache(TimeProvider? time = null)
    {
        var gitExe = GitExecutable.Find();
        Assert.NotNull(gitExe);
        return new GitStatusCache(gitExe, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5), time);
    }

    /// <summary>Espera o primeiro status em segundo plano (dirtyCount ou nota), no máximo 10 s.</summary>
    private static async Task<GitInfo?> SettleAsync(GitInfoResolver resolver, string path)
    {
        GitInfo? info = null;
        for (var i = 0; i < 100; i++)
        {
            info = resolver.Resolve(path, null);
            if (info?.DirtyCount is not null || info?.StatusNote is not null)
            {
                break;
            }

            await Task.Delay(100);
        }

        return info;
    }

    private static async Task<GitInfo> ResolveAsync(string path)
    {
        using var cache = NewCache();
        var info = await SettleAsync(new GitInfoResolver(cache), path);
        Assert.NotNull(info);
        return info;
    }

    private static void AssertSkipped(GitInfo info, string expectedInNote)
    {
        Assert.Equal("main", info.Branch);
        Assert.Null(info.DirtyCount);
        Assert.Null(info.Ahead);
        Assert.Null(info.Behind);
        Assert.NotNull(info.StatusNote);
        Assert.StartsWith("Status não lido", info.StatusNote, StringComparison.Ordinal);
        Assert.Contains(expectedInNote, info.StatusNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_DoesNotRunFsmonitorHookFromRepoConfig()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        var marker = Path.Combine(repo.Path, "fsmon-marker.txt");
        repo.Git("config", "core.fsmonitor", "echo pwned > fsmon-marker.txt");

        var info = await ResolveAsync(repo.Path);

        Assert.False(File.Exists(marker), "o hook core.fsmonitor do repositório foi executado");
        AssertSkipped(info, "fsmonitor");
    }

    [Fact]
    public async Task Status_SkipsRepoWithFilterDriverInConfig()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        repo.Git("config", "filter.evil.clean", "echo pwned > filter-marker.txt");

        AssertSkipped(await ResolveAsync(repo.Path), "filtro");
    }

    [Theory]
    [InlineData("include.path", "../../evil.config")]
    [InlineData("includeIf.gitdir:**.path", "evil.config")]
    public async Task Status_SkipsRepoWithIncludes(string key, string value)
    {
        using var repo = GitTestRepo.CreateWithCommit();
        repo.Git("config", key, value);

        AssertSkipped(await ResolveAsync(repo.Path), "include");
    }

    [Fact]
    public async Task Status_StillReadsRepoWhoseGitattributesUsesGlobalFilter()
    {
        // Git LFS: o .gitattributes só referencia o driver; o comando vem da config global do usuário.
        using var repo = GitTestRepo.CreateWithCommit();
        File.WriteAllText(Path.Combine(repo.Path, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");

        var info = await ResolveAsync(repo.Path);

        Assert.Null(info.StatusNote);
        Assert.Equal(1, info.DirtyCount);
    }

    [Fact]
    public async Task Status_SkipsWorktreeWithFsmonitorInConfigWorktree()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        var worktree = repo.Temp.Combine("wt");
        repo.Git("worktree", "add", "-b", "main2", worktree);
        repo.Git("config", "extensions.worktreeConfig", "true");
        GitTestRepo.RunGit(worktree, "config", "--worktree", "core.fsmonitor", "echo pwned > fsmon-marker.txt");

        var info = await ResolveAsync(worktree);

        Assert.False(File.Exists(Path.Combine(worktree, "fsmon-marker.txt")));
        Assert.Null(info.DirtyCount);
        Assert.Contains("fsmonitor", info.StatusNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_CleanRepoStillReportsDirtyCount()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        File.WriteAllText(Path.Combine(repo.Path, "novo.txt"), "x");

        var info = await ResolveAsync(repo.Path);

        Assert.Equal(1, info.DirtyCount);
        Assert.Null(info.StatusNote);
    }

    [Fact]
    public void StartInfo_DisablesHooksFsmonitorAndPrompts()
    {
        var psi = GitStatusCache.BuildStartInfo(@"C:\git\git.exe", @"C:\repo");

        var args = string.Join(' ', psi.ArgumentList);
        Assert.StartsWith("-c core.fsmonitor=false -c core.hooksPath=NUL -c core.untrackedCache=false --no-optional-locks -C C:\\repo status", args, StringComparison.Ordinal);
        Assert.Equal("0", psi.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("0", psi.Environment["GIT_OPTIONAL_LOCKS"]);
        Assert.False(psi.UseShellExecute);
    }

    [Theory]
    [InlineData("[core]\n\tbare = false\n\tfsmonitor = /tmp/hook\n", "fsmonitor")]
    [InlineData("[CORE]\n  FSMonitor=true", "fsmonitor")]
    [InlineData("[core] fsmonitor = hook", "fsmonitor")]
    [InlineData("[filter \"lfs\"]\n\tclean = git-lfs clean -- %f\n", "filtro")]
    [InlineData("[Filter.x]\n\tsmudge = x", "filtro")]
    [InlineData("  [include]\n\tpath = x", "include")]
    [InlineData("[includeIf \"gitdir:~/\"]\n\tpath = x", "include")]
    public async Task Inspect_FlagsDangerousConfig(string config, string expectedInNote)
    {
        var note = await GitRepoSafety.InspectConfigAsync(config);

        Assert.NotNull(note);
        Assert.Contains(expectedInNote, note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[core]\n\trepositoryformatversion = 0\n\tfilemode = false\n[remote \"origin\"]\n\turl = https://x/filter.git\n")]
    [InlineData("[core]\n\tfsmonitor = false\n")]
    [InlineData("# [filter \"x\"]\n; [include]\n[user]\n\tname = include\n")]
    [InlineData("")]
    public async Task Inspect_AcceptsOrdinaryConfig(string config)
    {
        Assert.Null(await GitRepoSafety.InspectConfigAsync(config));
    }

    // Q11
    [Fact]
    public void Cache_PrunesReposNotSeenForTenMinutes()
    {
        using var temp = new TempDirectory();
        var time = new ManualTimeProvider();
        using var cache = new GitStatusCache(@"C:\nao\existe\git.exe", TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(3), time);
        var a = new GitRepoLocation(temp.Combine("a"), temp.Combine("a", ".git"));
        var b = new GitRepoLocation(temp.Combine("b"), temp.Combine("b", ".git"));

        cache.TryGet(a);
        cache.TryGet(b);
        time.Advance(TimeSpan.FromMinutes(6));
        cache.TryGet(b);
        time.Advance(TimeSpan.FromMinutes(5));
        cache.TryGet(b);

        Assert.Equal(1, cache.Count);
    }
}
