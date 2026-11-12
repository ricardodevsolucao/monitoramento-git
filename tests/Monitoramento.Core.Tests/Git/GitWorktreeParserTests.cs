using Monitoramento.Core.Git;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Git;

/// <summary><c>git worktree list --porcelain -z</c> e a validação dos caminhos (dado do repositório, não confiável).</summary>
public sealed class GitWorktreeParserTests
{
    private const string Sha = "863a701955d78babcecd96da7b00947abf57b8a4";

    [Fact]
    public void Parse_ReadsAllAttributes()
    {
        var output = string.Join('\0',
            "worktree C:/src/app", "HEAD " + Sha, "branch refs/heads/main", "",
            "worktree C:/src/app.worktrees/app-feat", "HEAD " + Sha, "branch refs/heads/feat/x", "locked em uso no notebook", "",
            "worktree C:/src/old", "HEAD " + Sha, "detached", "prunable gitdir file points to non-existent location", "",
            "worktree C:/src/lock2", "HEAD " + Sha, "detached", "locked", "") + "\0";

        var list = GitWorktreeParser.Parse(output);

        Assert.Equal(4, list.Count);
        Assert.Equal(new RawWorktree("C:/src/app", Sha, "main", false, false, false, null, false), list[0]);
        Assert.Equal(new RawWorktree("C:/src/app.worktrees/app-feat", Sha, "feat/x", false, false, true, "em uso no notebook", false), list[1]);
        Assert.Equal(new RawWorktree("C:/src/old", Sha, null, true, false, false, null, true), list[2]);
        Assert.Equal(new RawWorktree("C:/src/lock2", Sha, null, true, false, true, null, false), list[3]);
    }

    [Fact]
    public void Parse_BareAndGarbage()
    {
        var output = "worktree C:/src/bare.git\0bare\0\0lixo sem worktree\0\0worktree \0\0";

        var list = GitWorktreeParser.Parse(output);

        var bare = Assert.Single(list);
        Assert.True(bare.Bare);
    }

    [Fact]
    public void LinkedWorktree_IsValid_AndPointsBackToMain()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        repo.Git("branch", "feat");
        var path = repo.Temp.Combine("wt-feat");
        repo.Git("worktree", "add", path, "feat");
        var commonDir = Path.Combine(repo.Path, ".git");

        var location = GitRepositoryLocator.Locate(path)!;

        Assert.Equal(path, location.RepoRoot);
        Assert.True(GitWorktrees.IsLinkedWorktree(location));
        Assert.Equal(repo.Path, GitWorktrees.MainRepoRoot(location));
        Assert.True(GitWorktrees.IsValidLinkedWorktree(path, commonDir));
        Assert.False(GitWorktrees.IsLinkedWorktree(GitRepositoryLocator.Locate(repo.Path)!));
        Assert.Null(GitWorktrees.MainRepoRoot(GitRepositoryLocator.Locate(repo.Path)!));
    }

    [Fact]
    public void ForeignWorktree_PointingToAnotherRepo_IsNotValidForThisOne()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        using var other = GitTestRepo.CreateWithCommit();
        other.Git("branch", "x");
        var foreign = other.Temp.Combine("wt-x");
        other.Git("worktree", "add", foreign, "x");

        Assert.False(GitWorktrees.IsValidLinkedWorktree(foreign, Path.Combine(repo.Path, ".git")));
        Assert.False(GitWorktrees.IsValidLinkedWorktree(repo.Path, Path.Combine(repo.Path, ".git"))); // o principal não é "vinculado"
        Assert.False(GitWorktrees.IsValidLinkedWorktree(@"\\servidor\share\wt", Path.Combine(repo.Path, ".git")));
    }

    [Fact]
    public void JunctionToWorktree_IsNotValid()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        repo.Git("branch", "feat");
        var path = repo.Temp.Combine("wt-feat");
        repo.Git("worktree", "add", path, "feat");
        var link = repo.Temp.Combine("atalho");
        GitConfigSafetyTests.Junction(link, path);

        Assert.False(GitWorktrees.IsValidLinkedWorktree(link, Path.Combine(repo.Path, ".git")));
    }

    [Fact]
    public void FindChildRepos_SkipsLinkedWorktreesOfSiblings()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        repo.Git("branch", "feat");
        var worktree = repo.Temp.Combine("wt-feat");
        repo.Git("worktree", "add", worktree, "feat");

        var children = GitRepositoryLocator.FindChildRepos(repo.Temp.Path).Select(l => l.RepoRoot).ToList();

        Assert.Equal([repo.Path], children);
    }

    [Fact]
    public void Resolver_ProcessInWorktree_ReportsMainRepoRoot()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        repo.Git("branch", "feat");
        var worktree = repo.Temp.Combine("wt-feat");
        repo.Git("worktree", "add", worktree, "feat");
        var resolver = new GitInfoResolver(statusCache: null);

        var info = resolver.Resolve(worktree, null)!;
        var main = resolver.Resolve(repo.Path, null)!;

        Assert.Equal(worktree, info.RepoRoot);
        Assert.Equal("feat", info.Branch);
        Assert.Equal(repo.Path, info.MainRepoRoot);
        Assert.Null(main.MainRepoRoot);
    }
}
