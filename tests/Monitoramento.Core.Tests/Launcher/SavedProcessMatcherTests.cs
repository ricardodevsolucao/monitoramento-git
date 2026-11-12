using Monitoramento.Core.Launcher;
using Monitoramento.Core.Models;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Launcher;

public sealed class SavedProcessMatcherTests
{
    private static readonly IReadOnlyList<ProcessInfo> Processes =
    [
        Proc.Info(100, "cmd.exe", 1, parentPid: 1, createTime: 1000, commandLine: "cmd.exe /c npm run dev", cwd: @"C:\proj"),
        Proc.Info(101, "node.exe", 1, parentPid: 100, createTime: 1010, commandLine: "node vite", cwd: @"C:\proj"),
        Proc.Info(102, "esbuild.exe", 1, parentPid: 101, createTime: 1020),
        Proc.Info(103, "old.exe", 1, parentPid: 100, createTime: 500), // pid reaproveitado: mais antigo que o pai
        Proc.Info(200, "cmd.exe", 1, parentPid: 1, createTime: 2000, commandLine: @"C:\Windows\system32\cmd.exe /k   NPM run   dev", cwd: @"c:\PROJ\"),
        Proc.Info(201, "cmd.exe", 1, parentPid: 1, createTime: 2000, commandLine: "cmd.exe /k npm run dev", cwd: @"C:\outro"),
        Proc.Info(202, "node.exe", 1, parentPid: 200, createTime: 2100),
    ];

    private static SavedProcess Saved(string cwd) => new() { Id = "s1", Name = "dev", Command = "npm run dev", WorkingDirectory = cwd };

    private static int[] Pids(IReadOnlyList<ProcessInfo> matches) => matches.Select(p => p.Pid).Order().ToArray();

    [Fact]
    public void Match_TrackedRootIncludesDescendantsButNotReusedPids()
    {
        var matcher = new SavedProcessMatcher(Processes);

        var result = matcher.Match(Saved(@"C:\nenhum"), new LaunchRecord(100, 1000, DateTimeOffset.UnixEpoch));

        Assert.Equal([100, 101, 102], Pids(result));
    }

    [Fact]
    public void Match_FindsDescendantsEvenAfterRootExited()
    {
        var withoutRoot = Processes.Where(p => p.Pid != 100).ToList();
        var matcher = new SavedProcessMatcher(withoutRoot);

        var result = matcher.Match(Saved(@"C:\nenhum"), new LaunchRecord(100, 1000, DateTimeOffset.UnixEpoch));

        Assert.Equal([101, 102], Pids(result));
    }

    [Fact]
    public void Match_ByWorkingDirectoryAndNormalizedCommand()
    {
        var matcher = new SavedProcessMatcher(Processes);

        var result = matcher.Match(Saved(@"C:\proj"), launch: null);

        Assert.Equal([100, 101, 102, 200, 202], Pids(result));
    }

    [Fact]
    public void Match_NothingWhenNotRunning()
    {
        var matcher = new SavedProcessMatcher(Processes);

        Assert.Empty(matcher.Match(Saved(@"C:\nenhum"), launch: null));
    }

    // Q3
    [Theory]
    [InlineData("cmd.exe /c npm run dev:api", false)]
    [InlineData("cmd.exe /c npm run devtools", false)]
    [InlineData("cmd.exe /c xnpm run dev", false)]
    [InlineData("cmd.exe /c npm run dev --host", true)]
    [InlineData("cmd.exe /s /c \"npm run dev\"", true)]
    [InlineData("npm run dev", true)]
    public void Match_RequiresTokenBoundaries(string commandLine, bool expected)
    {
        IReadOnlyList<ProcessInfo> processes = [Proc.Info(300, "cmd.exe", 1, createTime: 1, commandLine: commandLine, cwd: @"C:\proj")];

        var result = new SavedProcessMatcher(processes).Match(Saved(@"C:\proj"), launch: null);

        Assert.Equal(expected, result.Count == 1);
    }

    [Theory]
    [InlineData("  NPM   run\tdev ", "npm run dev")]
    [InlineData(null, "")]
    public void CommandText_NormalizesCaseAndWhitespace(string? input, string expected)
    {
        Assert.Equal(expected, CommandText.Normalize(input));
    }

    [Theory]
    [InlineData(@"C:\Proj\", @"c:\proj", true)]
    [InlineData(@"C:\proj", @"C:\proj2", false)]
    [InlineData(null, @"C:\proj", false)]
    public void PathText_ComparesIgnoringCaseAndTrailingSeparator(string? a, string? b, bool expected)
    {
        Assert.Equal(expected, PathText.AreEqual(a, b));
    }
}
