using Monitoramento.Core.Collection;

namespace Monitoramento.Core.Metrics;

/// <summary>
/// CPU% por processo: delta(kernel+user) / (delta tempo × núcleos lógicos) × 100.
/// Identifica o processo por (pid, createTime) para não misturar pids reaproveitados.
/// Amostras com menos de <see cref="MinElapsed"/> entre si não recalculam (a granularidade do tempo de CPU, ~15,6 ms,
/// saturaria em 100%): devolvem o último valor e o próximo delta é medido desde a última amostra aceita.
/// </summary>
internal sealed class CpuUsageTracker
{
    /// <summary>250 ms em unidades de 100 ns.</summary>
    public const long MinElapsed = 2_500_000;

    private Dictionary<(int Pid, long CreateTime), long> _previous = [];
    private Dictionary<(int Pid, long CreateTime), double> _lastPercent = [];
    private long? _previousTimestamp;

    /// <param name="timestamp">Instante da amostra em unidades de 100 ns (monotônico).</param>
    /// <returns>CPU% de cada processo, na mesma ordem de <paramref name="processes"/>.</returns>
    public double[] Update(IReadOnlyList<RawProcess> processes, long timestamp, int logicalCores)
    {
        var result = new double[processes.Count];
        if (_previousTimestamp is { } last && timestamp - last < MinElapsed)
        {
            for (var i = 0; i < processes.Count; i++)
            {
                result[i] = _lastPercent.GetValueOrDefault((processes[i].Pid, processes[i].CreateTime));
            }

            return result;
        }

        var current = new Dictionary<(int, long), long>(processes.Count);
        var percents = new Dictionary<(int, long), double>(processes.Count);
        var elapsed = _previousTimestamp is { } previous ? timestamp - previous : 0;

        for (var i = 0; i < processes.Count; i++)
        {
            var p = processes[i];
            var key = (p.Pid, p.CreateTime);
            current[key] = p.CpuTime;

            if (elapsed > 0 && _previous.TryGetValue(key, out var before))
            {
                result[i] = Percent(p.CpuTime - before, elapsed, logicalCores);
            }

            percents[key] = result[i];
        }

        _previous = current;
        _lastPercent = percents;
        _previousTimestamp = timestamp;
        return result;
    }

    public static double Percent(long cpuDelta, long elapsed, int logicalCores)
    {
        if (cpuDelta <= 0 || elapsed <= 0 || logicalCores <= 0)
        {
            return 0;
        }

        return Math.Clamp(cpuDelta * 100.0 / ((double)elapsed * logicalCores), 0, 100);
    }
}

/// <summary>
/// CPU% da máquina a partir de GetSystemTimes (o tempo de kernel já inclui o ocioso). Mesma regra de intervalo mínimo
/// do <see cref="CpuUsageTracker"/>.
/// </summary>
internal sealed class MachineCpuTracker
{
    private SystemCpuTimes? _previous;
    private long? _previousTimestamp;
    private double _last;

    /// <param name="timestamp">Instante da amostra em unidades de 100 ns; null ignora o intervalo mínimo.</param>
    public double Update(SystemCpuTimes times, long? timestamp = null)
    {
        if (timestamp is { } now && _previousTimestamp is { } last && now - last < CpuUsageTracker.MinElapsed)
        {
            return _last;
        }

        var previous = _previous;
        _previous = times;
        _previousTimestamp = timestamp;
        if (previous is not { } before)
        {
            return _last = 0;
        }

        var total = (times.Kernel - before.Kernel) + (times.User - before.User);
        var idle = times.Idle - before.Idle;
        return _last = total <= 0 ? 0 : Math.Clamp((total - idle) * 100.0 / total, 0, 100);
    }
}
