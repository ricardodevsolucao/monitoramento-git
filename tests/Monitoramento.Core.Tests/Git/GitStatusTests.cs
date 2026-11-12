using Monitoramento.Core.Git;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Git;

public sealed class GitStatusTests
{
    [Fact]
    public void Parse_CountsChangedRenamedUnmergedAndUntracked()
    {
        const string output = """
            # branch.oid 1234567890abcdef
            # branch.head main
            # branch.upstream origin/main
            # branch.ab +2 -1
            1 .M N... 100644 100644 100644 aaaa bbbb file.txt
            2 R. N... 100644 100644 100644 aaaa bbbb R100 new.txt	old.txt
            u UU N... 100644 100644 100644 100644 aaaa bbbb cccc conflict.txt
            ? untracked.txt
            ! ignored.txt
            """;

        var summary = PorcelainV2Parser.Parse(output);

        Assert.Equal(new GitStatusSummary(DirtyCount: 4, Ahead: 2, Behind: 1), summary);
    }

    [Fact]
    public void Parse_WithoutUpstreamHasNullAheadBehind()
    {
        const string output = "# branch.oid abc\n# branch.head main\n? a.txt\n";

        Assert.Equal(new GitStatusSummary(1, null, null), PorcelainV2Parser.Parse(output));
    }

    [Fact]
    public void Parse_CleanTreeHasZeroDirty()
    {
        Assert.Equal(0, PorcelainV2Parser.Parse("# branch.ab +0 -0\r\n").DirtyCount);
    }

    [Fact]
    public void Parse_IgnoresMalformedAheadBehind()
    {
        var summary = PorcelainV2Parser.Parse("# branch.ab +x -y\n");

        Assert.Null(summary.Ahead);
        Assert.Null(summary.Behind);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Git\cmd\git.exe", @"C:\Program Files\Git")]
    [InlineData(@"C:\Program Files\Git\bin\git.exe", @"C:\Program Files\Git")]
    [InlineData(@"C:\Program Files\Git\mingw64\bin\git.exe", @"C:\Program Files\Git")]
    [InlineData(@"D:\tools\git.exe", @"D:\tools")]
    public void InstallDirectory_IsDerivedFromGitExe(string gitExe, string expected)
    {
        Assert.Equal(expected, GitExecutable.InstallDirectoryOf(gitExe));
    }

    [Theory]
    [InlineData("git.exe", null, true)]
    [InlineData("GIT.EXE", null, true)]
    [InlineData("git-remote-https.exe", null, true)]
    [InlineData("git-credential-manager.exe", null, true)]
    [InlineData("ssh.exe", @"C:\Program Files\Git\usr\bin\ssh.exe", true)]
    [InlineData("ssh.exe", @"C:\Windows\System32\OpenSSH\ssh.exe", false)]
    [InlineData("x.exe", @"C:\Program Files\GitHub\x.exe", false)]
    [InlineData("code.exe", null, false)]
    public void Classifier_DetectsGitProcesses(string name, string? exePath, bool expected)
    {
        var classifier = new GitProcessClassifier(@"C:\Program Files\Git");

        Assert.Equal(expected, classifier.IsGitProcess(name, exePath));
    }

    [Fact]
    public async Task StatusCache_ComputesInBackgroundAndReturnsSummary()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        File.WriteAllText(Path.Combine(repo.Path, "novo.txt"), "x");
        var gitExe = GitExecutable.Find();
        Assert.NotNull(gitExe);
        using var cache = new GitStatusCache(gitExe, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(3));

        var location = GitRepositoryLocator.Locate(repo.Path)!;
        var summary = await PollAsync(() => cache.TryGet(location)?.Summary);

        Assert.NotNull(summary);
        Assert.Equal(1, summary.DirtyCount);
        Assert.Null(summary.Ahead);
    }

    [Fact]
    public async Task StatusCache_InvalidGitExecutableNeverThrows()
    {
        using var temp = new TempDirectory();
        using var cache = new GitStatusCache(@"C:\nao\existe\git.exe", TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(3));
        var location = new GitRepoLocation(temp.Path, Path.Combine(temp.Path, ".git"));

        Assert.Null(cache.TryGet(location)?.Summary);
        Assert.Null(cache.TryGet(location)?.Summary);
        await Task.Delay(300);
        Assert.Null(cache.TryGet(location)?.Summary);
    }

    private static async Task<GitStatusSummary?> PollAsync(Func<GitStatusSummary?> read)
    {
        for (var i = 0; i < 100; i++)
        {
            var value = read();
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(100);
        }

        return null;
    }
}
