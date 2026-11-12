using Monitoramento.Core.Models;

namespace Monitoramento.Core.Metrics;

/// <summary>Histórico de memória/CPU da máquina dentro de uma janela de tempo (padrão: 10 min).</summary>
internal sealed class MachineHistory(TimeSpan window)
{
    private readonly Queue<MachineHistoryPoint> _points = new();
    private readonly long _windowMs = (long)window.TotalMilliseconds;

    public IReadOnlyList<MachineHistoryPoint> Points => [.. _points];

    public void Add(MachineHistoryPoint point)
    {
        _points.Enqueue(point);
        while (_points.TryPeek(out var oldest) && point.T - oldest.T > _windowMs)
        {
            _points.Dequeue();
        }
    }
}
