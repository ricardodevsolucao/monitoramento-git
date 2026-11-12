using Monitoramento.Core.Grouping;
using Monitoramento.Core.Metrics;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Tests.Metrics;

public sealed class HistoryTests
{
    [Fact]
    public void GroupMemoryHistory_KeepsOnlyLastSixtyPoints()
    {
        var history = new GroupMemoryHistory(60);
        IReadOnlyList<long> points = [];

        for (var i = 1; i <= 70; i++)
        {
            points = history.Push("app:x.exe", i);
        }

        Assert.Equal(60, points.Count);
        Assert.Equal(11, points[0]);
        Assert.Equal(70, points[^1]);
    }

    [Fact]
    public void GroupMemoryHistory_RetainOnlyDropsVanishedGroups()
    {
        var history = new GroupMemoryHistory(60);
        history.Push("app:a.exe", 1);
        history.Push("app:b.exe", 1);

        history.RetainOnly(new HashSet<string> { "app:a.exe" });

        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void MachineHistory_DropsPointsOlderThanWindow()
    {
        var history = new MachineHistory(TimeSpan.FromMinutes(10));
        var start = 1_700_000_000_000L;

        history.Add(new MachineHistoryPoint(start, 10, 1));
        history.Add(new MachineHistoryPoint(start + (5 * 60_000), 20, 2));
        history.Add(new MachineHistoryPoint(start + (11 * 60_000), 30, 3));

        Assert.Equal([20.0, 30.0], history.Points.Select(p => p.MemoryPercent));
    }
}
