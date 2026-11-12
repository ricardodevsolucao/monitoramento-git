using Monitoramento.Core.Git;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Git;

public sealed class GitRepositoryLocatorTests
{
    [Fact]
    public void Locate_FindsRootWalkingUpFromSubfolder()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        var deep = Directory.CreateDirectory(Path.Combine(repo.Path, "src", "deep")).FullName;

        var location = GitRepositoryLocator.Locate(deep);

        Assert.NotNull(location);
        Assert.Equal(repo.Path, location.RepoRoot, ignoreCase: true);
        Assert.Equal("main", GitRepositoryLocator.ReadBranch(location.GitDir));
    }

    [Fact]
    public void ReadBranch_DetachedHeadReturnsShortSha()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        repo.Git("checkout", "--detach");
        var sha = repo.Git("rev-parse", "HEAD");

        var location = GitRepositoryLocator.Locate(repo.Path);

        Assert.NotNull(location);
        Assert.Equal(sha[..7], GitRepositoryLocator.ReadBranch(location.GitDir));
    }

    [Fact]
    public void Locate_FollowsGitdirFileOfWorktree()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        var worktree = repo.Temp.Combine("wt");
        repo.Git("worktree", "add", "-b", "feature/x", worktree);
        Assert.True(File.Exists(Path.Combine(worktree, ".git")));

        var location = GitRepositoryLocator.Locate(worktree);

        Assert.NotNull(location);
        Assert.Equal(worktree, location.RepoRoot, ignoreCase: true);
        Assert.Equal("feature/x", GitRepositoryLocator.ReadBranch(location.GitDir));
    }

    [Fact]
    public void Locate_ReturnsNullOutsideRepository()
    {
        using var temp = new TempDirectory();
        var folder = Directory.CreateDirectory(temp.Combine("solta")).FullName;

        Assert.Null(GitRepositoryLocator.Locate(folder));
    }

    [Fact]
    public void Locate_ReturnsNullForMissingFolder()
    {
        Assert.Null(GitRepositoryLocator.Locate(@"Z:\nao\existe\mesmo"));
    }

    [Fact]
    public void Resolver_BuildsGitInfoWithRepoNameAndBranch()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        var resolver = new GitInfoResolver(statusCache: null);

        var info = resolver.Resolve(repo.Path, commandLine: null);

        Assert.NotNull(info);
        Assert.Equal("repo", info.RepoName);
        Assert.Equal("main", info.Branch);
    }

    [Fact]
    public void Resolver_UsesAbsolutePathInCommandLineWhenCwdIsUnknown()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        var script = Path.Combine(repo.Path, "readme.txt");

        var info = new GitInfoResolver(statusCache: null).Resolve(null, $"node \"{script}\" --watch");

        Assert.NotNull(info);
        Assert.Equal(repo.Path, info.RepoRoot, ignoreCase: true);
    }
}
