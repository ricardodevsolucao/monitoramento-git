using Monitoramento.Core.Grouping;
using Monitoramento.Core.Models;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Grouping;

public sealed class AppGrouperTests
{
    private static readonly GitInfo RepoA = new(@"C:\src\a", "a", "main", 0, 0, 0);
    private static readonly GitInfo RepoB = new(@"C:\src\b", "b", "dev", null, null, null);

    private static IReadOnlyList<AppGroup> BuildSample(IReadOnlySet<string>? favorites = null) =>
        AppGrouper.Build(
            [
                Proc.Info(1, "Code.exe", 300, cpu: 1.5, exePath: @"C:\VS Code\Code.exe"),
                Proc.Info(2, "code.exe", 200, cpu: 2.5),
                Proc.Info(3, "chrome.exe", 1000, cpu: 10),
                Proc.Info(4, "node.exe", 50, git: RepoA),
                Proc.Info(5, "node.exe", 60, git: RepoA with { RepoRoot = @"c:\SRC\A" }),
                Proc.Info(6, "node.exe", 10, git: RepoB),
            ],
            totalMemoryBytes: 10_000,
            favorites ?? new HashSet<string>(),
            new FakeNames(),
            new GroupMemoryHistory(60));

    [Fact]
    public void Build_GroupsByExeNameIgnoringCase()
    {
        var groups = BuildSample();

        Assert.Equal(["app:chrome.exe", "app:code.exe", "app:node.exe"], groups.Select(g => g.Key));
        Assert.Equal(2, groups.Single(g => g.Key == "app:code.exe").ProcessCount);
    }

    [Fact]
    public void Build_OrdersGroupsAndProcessesByMemoryDescending()
    {
        var groups = BuildSample();

        Assert.Equal([1000L, 500L, 120L], groups.Select(g => g.MemoryBytes));
        Assert.Equal([5, 4, 6], groups.Single(g => g.Key == "app:node.exe").Processes.Select(p => p.Pid));
    }

    [Fact]
    public void Build_SumsMemoryAndCpuAndComputesPercent()
    {
        var code = BuildSample().Single(g => g.Key == "app:code.exe");

        Assert.Equal(500, code.MemoryBytes);
        Assert.Equal(5.0, code.MemoryPercent, precision: 6);
        Assert.Equal(4.0, code.CpuPercent, precision: 6);
    }

    [Fact]
    public void Build_UsesKnownExePathForIconKeyAndFriendlyName()
    {
        var code = BuildSample().Single(g => g.Key == "app:code.exe");

        Assert.Equal(@"C:\VS Code\Code.exe", code.ExePath);
        Assert.Equal(@"c:\vs code\code.exe", code.IconKey);
        Assert.Equal("Nome Code", code.Name);
    }

    [Fact]
    public void Build_IconKeyIsNullWhenNoPathIsKnown()
    {
        var chrome = BuildSample().Single(g => g.Key == "app:chrome.exe");

        Assert.Null(chrome.ExePath);
        Assert.Null(chrome.IconKey);
    }

    [Fact]
    public void Build_ListsDistinctReposIgnoringCase()
    {
        var node = BuildSample().Single(g => g.Key == "app:node.exe");

        Assert.Equal(2, node.Repos.Count);
        Assert.Contains(node.Repos, r => r.RepoName == "b");
    }

    [Fact]
    public void Build_MarksFavoriteGroups()
    {
        var groups = BuildSample(new HashSet<string> { "app:node.exe" });

        Assert.True(groups.Single(g => g.Key == "app:node.exe").Favorite);
        Assert.False(groups.Single(g => g.Key == "app:code.exe").Favorite);
    }

    [Fact]
    public void Build_RecordsMemoryHistoryPerGroup()
    {
        var history = new GroupMemoryHistory(60);
        IReadOnlyList<ProcessInfo> first = [Proc.Info(1, "a.exe", 100)];
        IReadOnlyList<ProcessInfo> second = [Proc.Info(1, "a.exe", 150)];

        AppGrouper.Build(first, 1000, new HashSet<string>(), new FakeNames(), history);
        var groups = AppGrouper.Build(second, 1000, new HashSet<string>(), new FakeNames(), history);

        Assert.Equal([100L, 150L], groups[0].MemoryHistory);
    }

    [Fact]
    public void Build_ClampsGroupCpuAtOneHundred()
    {
        var groups = AppGrouper.Build(
            [Proc.Info(1, "a.exe", 1, cpu: 70), Proc.Info(2, "a.exe", 1, cpu: 70)],
            1000,
            new HashSet<string>(),
            new FakeNames(),
            new GroupMemoryHistory(60));

        Assert.Equal(100, groups[0].CpuPercent);
    }
}
