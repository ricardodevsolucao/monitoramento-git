using Monitoramento.Core.Git;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Git;

/// <summary>Workspace que não é repositório, mas tem repositórios nas subpastas imediatas (ex.: gsintegra\app e \api).</summary>
public sealed class ChildReposTests
{
    /// <summary>Repositório mínimo para o localizador (só a pasta .git com HEAD; o git não é chamado).</summary>
    private static string FakeRepo(TempDirectory temp, params string[] parts)
    {
        var path = temp.Combine(parts);
        var gitDir = Directory.CreateDirectory(Path.Combine(path, ".git")).FullName;
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");
        return path;
    }

    [Fact]
    public void FindChildRepos_FindsImmediateReposAndSkipsIgnoredFolders()
    {
        using var temp = new TempDirectory();
        var api = FakeRepo(temp, "api");
        var app = FakeRepo(temp, "app");
        FakeRepo(temp, "node_modules");
        FakeRepo(temp, "bin");
        FakeRepo(temp, "obj");
        FakeRepo(temp, "dist");
        FakeRepo(temp, ".oculta");
        var hidden = FakeRepo(temp, "escondida");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        FakeRepo(temp, "docs", "neto"); // 2 níveis: fora
        Directory.CreateDirectory(temp.Combine("sem-git"));

        var found = GitRepositoryLocator.FindChildRepos(temp.Path);

        Assert.Equal([api, app], found.Select(l => l.RepoRoot));
    }

    [Fact]
    public void FindChildRepos_StopsAtTwelve()
    {
        using var temp = new TempDirectory();
        for (var i = 0; i < 15; i++)
        {
            FakeRepo(temp, $"repo{i:00}");
        }

        Assert.Equal(GitRepositoryLocator.MaxChildRepos, GitRepositoryLocator.FindChildRepos(temp.Path).Count);
        Assert.Equal(12, GitRepositoryLocator.MaxChildRepos);
    }

    [Theory]
    [InlineData(@"\\servidor\share")]
    [InlineData("relativa")]
    [InlineData(null)]
    public void FindChildRepos_NonLocalFolder_IsEmpty(string? path)
    {
        Assert.Empty(GitRepositoryLocator.FindChildRepos(path));
    }

    [Fact]
    public void ResolveChildren_InsideRepo_IsEmpty()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        FakeRepo(repo.Temp, "repo", "vendor"); // repositório aninhado dentro do repo: não conta
        var resolver = new GitInfoResolver(null);

        Assert.Empty(resolver.ResolveChildren(repo.Path));
    }

    [Fact]
    public void ResolveChildren_ReturnsGitInfoWithBranch()
    {
        using var temp = new TempDirectory();
        var app = FakeRepo(temp, "app");
        var resolver = new GitInfoResolver(null);

        var info = Assert.Single(resolver.ResolveChildren(temp.Path));

        Assert.Equal(app, info.RepoRoot);
        Assert.Equal("app", info.RepoName);
        Assert.Equal("main", info.Branch);
    }

    [Fact]
    public void ResolveChildren_CachesTheScanForSixtySeconds()
    {
        using var temp = new TempDirectory();
        FakeRepo(temp, "app");
        var time = new ManualTimeProvider();
        var resolver = new GitInfoResolver(null, time);

        Assert.Single(resolver.ResolveChildren(temp.Path));
        FakeRepo(temp, "api");
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Single(resolver.ResolveChildren(temp.Path)); // sem nova varredura

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2, resolver.ResolveChildren(temp.Path).Count);
    }
}
