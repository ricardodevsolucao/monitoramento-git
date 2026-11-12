using Monitoramento.Core.Git;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Tests.Git;

/// <summary>Parsers da saída do git usada na visão Git (log, status -z e for-each-ref).</summary>
public sealed class GitOutputParsersTests
{
    private const string ShaA = "14ebee655c26b06b92f04436c8d3cbfb3d08ff74";
    private const string ShaB = "b6d60958079101c88dbf71146f2e9c3334eae9cd";
    private const string ShaC = "503a3711b61ac2118e544410eb9b944e3605cd84";

    private static string Record(string sha, string parents, string author, string date, string decorations, string subject) =>
        string.Join('\0', sha, parents, author, date, decorations, subject);

    [Fact]
    public void Log_ParsesRecordsRefsAndSpecialSubjects()
    {
        var subject = "fix: a, b -> c \u001f \"aspas\" 'x' %x00 ação — 漢字 <tag>";
        var output = string.Join('\0',
            Record(ShaA, ShaC + " " + ShaB, "Fulano de Tal", "2026-11-12T00:19:18-03:00",
                "HEAD -> refs/heads/main, refs/remotes/origin/main, refs/remotes/origin/HEAD, tag: refs/tags/v1, refs/stash", subject),
            Record(ShaC, string.Empty, "Ciclano", "2026-11-11T10:00:00Z", "refs/heads/feat,x", "inicial"));

        var entries = GitLogParser.Parse(output);

        Assert.Equal(2, entries.Count);
        var merge = entries[0];
        Assert.Equal(ShaA, merge.Sha);
        Assert.Equal([ShaC, ShaB], merge.Parents);
        Assert.Equal("Fulano de Tal", merge.Author);
        Assert.Equal("2026-11-12T00:19:18-03:00", merge.Date);
        Assert.Equal(subject, merge.Subject);
        Assert.Equal(
            [new GitRef("main", GitRefKind.Head), new GitRef("origin/main", GitRefKind.Remote), new GitRef("v1", GitRefKind.Tag)],
            merge.Refs);

        var root = entries[1];
        Assert.Empty(root.Parents);
        Assert.Equal([new GitRef("feat,x", GitRefKind.Branch)], root.Refs);
    }

    [Fact]
    public void Log_DetachedHeadIsHeadRef()
    {
        var entries = GitLogParser.Parse(Record(ShaA, string.Empty, "a", "2026-01-01T00:00:00Z", "HEAD, refs/heads/main", "x"));

        Assert.Equal([new GitRef("HEAD", GitRefKind.Head), new GitRef("main", GitRefKind.Branch)], entries[0].Refs);
    }

    [Fact]
    public void Log_IgnoresIncompleteTrailingRecordAndTrailingNul()
    {
        var output = Record(ShaA, string.Empty, "a", "d", string.Empty, "um") + "\0" + ShaC + "\0" + "x";

        var entries = GitLogParser.Parse(output);

        Assert.Single(entries);
        Assert.Equal("um", entries[0].Subject);
        Assert.Single(GitLogParser.Parse(Record(ShaA, string.Empty, "a", "d", string.Empty, "um") + "\0"));
        Assert.Empty(GitLogParser.Parse(string.Empty));
    }

    [Fact]
    public void Status_ParsesHeadersRenameConflictUntrackedAndIgnoresIgnored()
    {
        var output = string.Join('\0',
            "# branch.oid " + ShaA,
            "# branch.head main",
            "# branch.upstream origin/main",
            "# branch.ab +2 -1",
            "1 .M N... 100644 100644 100644 aaaa bbbb mod ificado.txt",
            "1 A. N... 000000 100644 100644 0000 bbbb novo.txt",
            "1 MD N... 100644 100644 000000 aaaa bbbb ambos.txt",
            "2 R. N... 100644 100644 100644 aaaa bbbb R100 pasta/novo nome.txt",
            "pasta/antigo.txt",
            "u UU N... 100644 100644 100644 100644 aaaa bbbb cccc conflito.txt",
            "? não rastreado.txt",
            "! ignorado.txt",
            string.Empty);

        var status = PorcelainV2Parser.ParseDetailed(output);

        Assert.Equal(ShaA, status.Oid);
        Assert.Equal("main", status.Head);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(2, status.Ahead);
        Assert.Equal(1, status.Behind);
        Assert.Equal(
            [
                new GitChange("mod ificado.txt", null, null, GitWorktreeState.M, false),
                new GitChange("novo.txt", null, GitIndexState.A, null, false),
                new GitChange("ambos.txt", null, GitIndexState.M, GitWorktreeState.D, false),
                new GitChange("pasta/novo nome.txt", "pasta/antigo.txt", GitIndexState.R, null, false),
                new GitChange("conflito.txt", null, GitIndexState.U, GitWorktreeState.U, true),
                new GitChange("não rastreado.txt", null, null, GitWorktreeState.Untracked, false),
            ],
            status.Changes);
    }

    [Fact]
    public void Status_InitialAndDetachedHeaders()
    {
        Assert.Null(PorcelainV2Parser.ParseDetailed("# branch.oid (initial)\0# branch.head main\0").Oid);
        Assert.Equal("(detached)", PorcelainV2Parser.ParseDetailed("# branch.oid abc\0# branch.head (detached)\0").Head);
    }

    [Fact]
    public void ForEachRef_ParsesLocalAndRemoteBranchesAndSkipsSymbolicRefs()
    {
        static string Line(params string[] fields) => string.Join('\0', fields);
        var output = string.Join('\n',
            Line("refs/heads/feat", "feat", ShaC, string.Empty, string.Empty, "2026-11-10T09:00:00-03:00", " ", string.Empty),
            Line("refs/heads/main", "main", ShaA, "origin/main", "ahead 2, behind 1", "2026-11-12T00:19:18-03:00", "*", string.Empty),
            Line("refs/heads/sync", "sync", ShaA, "origin/sync", string.Empty, "2026-11-12T00:00:00Z", " ", string.Empty),
            Line("refs/heads/old", "old", ShaA, "origin/old", "gone", "2026-11-12T00:00:00Z", " ", string.Empty),
            Line("refs/heads/ahead", "ahead", ShaA, "origin/ahead", "ahead 3", "2026-11-12T00:00:00Z", " ", string.Empty),
            Line("refs/remotes/origin/HEAD", "origin", ShaA, string.Empty, string.Empty, "2026-11-12T00:00:00Z", " ", "refs/remotes/origin/main"),
            Line("refs/remotes/origin/main", "origin/main", ShaC, string.Empty, string.Empty, "2026-11-11T00:00:00Z", " ", string.Empty),
            string.Empty);

        var branches = GitRefParser.Parse(output);

        Assert.Equal(
            [
                new GitBranch("feat", false, false, null, null, null, ShaC, "2026-11-10T09:00:00-03:00"),
                new GitBranch("main", true, false, "origin/main", 2, 1, ShaA, "2026-11-12T00:19:18-03:00"),
                new GitBranch("sync", false, false, "origin/sync", 0, 0, ShaA, "2026-11-12T00:00:00Z"),
                new GitBranch("old", false, false, "origin/old", null, null, ShaA, "2026-11-12T00:00:00Z"),
                new GitBranch("ahead", false, false, "origin/ahead", 3, 0, ShaA, "2026-11-12T00:00:00Z"),
                new GitBranch("origin/main", false, true, null, null, null, ShaC, "2026-11-11T00:00:00Z"),
            ],
            branches);
    }

    [Theory]
    [InlineData("[gpg]\n\tprogram = c:/x/evil.exe\n", "gpg")]
    [InlineData("[gpg \"ssh\"]\n\tprogram = x\n", "gpg")]
    [InlineData("[log]\n\tshowSignature = true\n", "showSignature")]
    [InlineData("[LOG]\n\tshowsignature\n", "showSignature")]
    [InlineData("[core]\n\talternateRefsCommand = x\n", "alternateRefsCommand")]
    [InlineData("[diff \"bin\"]\n\ttextconv = x\n", "diff")]
    [InlineData("[diff \"tool\"]\n\tcommand = x\n", "diff")]
    [InlineData("[diff]\n\texternal = x\n", "diff")]
    [InlineData("[include]\n\tpath = x\n", "include")]
    public async Task InspectLog_FlagsConfigThatRunsCodeDuringLog(string config, string expected)
    {
        var reason = await GitRepoSafety.InspectLogConfigAsync(config);

        Assert.NotNull(reason);
        Assert.Contains(expected, reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[log]\n\tshowSignature = false\n\tdecorate = short\n")]
    [InlineData("[diff]\n\trenames = true\n[core]\n\tautocrlf = true\n")]
    [InlineData("[filter \"lfs\"]\n\tclean = git-lfs clean -- %f\n")]
    [InlineData("[core]\n\tfsmonitor = true\n")]
    [InlineData("# [gpg]\n[user]\n\tsigningkey = gpg\n")]
    public async Task InspectLog_AcceptsConfigHarmlessForLog(string config)
    {
        Assert.Null(await GitRepoSafety.InspectLogConfigAsync(config));
    }
}
