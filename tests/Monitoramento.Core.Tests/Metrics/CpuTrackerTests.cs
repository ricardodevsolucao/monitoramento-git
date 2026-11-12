using Monitoramento.Core.Collection;
using Monitoramento.Core.Metrics;

namespace Monitoramento.Core.Tests.Metrics;

public sealed class CpuTrackerTests
{
    private const long OneSecond = 10_000_000; // unidades de 100 ns

    private static RawProcess Raw(int pid, long cpu, long create) => new(pid, 1, "x.exe", 0, cpu, create);

    [Fact]
    public void Update_FirstSampleIsZero()
    {
        var tracker = new CpuUsageTracker();

        var result = tracker.Update([Raw(10, cpu: 5_000_000, create: 1)], timestamp: 0, logicalCores: 4);

        Assert.Equal(0, result[0]);
    }

    [Fact]
    public void Update_NormalizesDeltaByElapsedTimeAndCores()
    {
        var tracker = new CpuUsageTracker();
        tracker.Update([Raw(10, cpu: 0, create: 1)], timestamp: 0, logicalCores: 4);

        // 0,2 s de CPU em 1 s com 4 núcleos = 5%
        var result = tracker.Update([Raw(10, cpu: 2_000_000, create: 1)], timestamp: OneSecond, logicalCores: 4);

        Assert.Equal(5.0, result[0], precision: 6);
    }

    [Fact]
    public void Update_ReusedPidWithDifferentCreateTimeStartsFromZero()
    {
        var tracker = new CpuUsageTracker();
        tracker.Update([Raw(10, cpu: 0, create: 1)], timestamp: 0, logicalCores: 4);

        var result = tracker.Update([Raw(10, cpu: 3_000_000, create: 99)], timestamp: OneSecond, logicalCores: 4);

        Assert.Equal(0, result[0]);
    }

    [Fact]
    public void Update_ClampsToOneHundred()
    {
        var tracker = new CpuUsageTracker();
        tracker.Update([Raw(10, cpu: 0, create: 1)], timestamp: 0, logicalCores: 1);

        var result = tracker.Update([Raw(10, cpu: 5 * OneSecond, create: 1)], timestamp: OneSecond, logicalCores: 1);

        Assert.Equal(100, result[0]);
    }

    // Q9
    [Fact]
    public void Update_IgnoresDeltaWhenSamplesAreTooClose()
    {
        var tracker = new CpuUsageTracker();
        tracker.Update([Raw(10, cpu: 0, create: 1)], timestamp: 0, logicalCores: 1);
        tracker.Update([Raw(10, cpu: 500_000, create: 1)], timestamp: OneSecond, logicalCores: 1); // 5%

        // 10 ms depois, um quantum de agendamento (15,6 ms) daria 100%
        var close = tracker.Update([Raw(10, cpu: 656_250, create: 1)], timestamp: OneSecond + 100_000, logicalCores: 1);
        var later = tracker.Update([Raw(10, cpu: 1_000_000, create: 1)], timestamp: 2 * OneSecond, logicalCores: 1);

        Assert.Equal(5.0, close[0], precision: 6);
        Assert.Equal(5.0, later[0], precision: 6); // delta medido desde a última amostra aceita
    }

    [Fact]
    public void MachineCpu_UsesBusyShareOfKernelPlusUser()
    {
        var tracker = new MachineCpuTracker();
        Assert.Equal(0, tracker.Update(new SystemCpuTimes(Idle: 100, Kernel: 200, User: 50)));

        // kernel inclui o tempo ocioso: ocupado = (8 + 2 - 6) / (8 + 2) = 40%
        var percent = tracker.Update(new SystemCpuTimes(Idle: 106, Kernel: 208, User: 52));

        Assert.Equal(40.0, percent, precision: 6);
    }
}
