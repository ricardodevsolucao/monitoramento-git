namespace Monitoramento.Core.Grouping;

/// <summary>Últimos N valores de memória por grupo (sparkline).</summary>
internal sealed class GroupMemoryHistory(int capacity)
{
    private readonly Dictionary<string, Queue<long>> _series = new(StringComparer.Ordinal);

    public int Count => _series.Count;

    public IReadOnlyList<long> Push(string key, long value)
    {
        if (!_series.TryGetValue(key, out var queue))
        {
            queue = new Queue<long>(capacity);
            _series[key] = queue;
        }

        if (queue.Count == capacity)
        {
            queue.Dequeue();
        }

        queue.Enqueue(value);
        return [.. queue];
    }

    public void RetainOnly(IReadOnlySet<string> keys)
    {
        foreach (var key in _series.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            _series.Remove(key);
        }
    }
}
