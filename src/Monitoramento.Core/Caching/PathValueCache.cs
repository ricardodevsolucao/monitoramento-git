using System.Collections.Concurrent;

namespace Monitoramento.Core.Caching;

/// <summary>
/// Cache por caminho de executável para leituras caras (ícone, nome amigável). Resultado nulo ("não consegui")
/// expira em <see cref="NegativeTtl"/>; entradas não pedidas há mais de <see cref="UnseenTtl"/> são podadas.
/// Seguro para várias threads.
/// </summary>
internal sealed class PathValueCache<TValue>
    where TValue : class
{
    public static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan UnseenTtl = TimeSpan.FromMinutes(10);
    private static readonly long PruneEveryMs = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _lastPruneMs;

    public PathValueCache(TimeProvider time)
    {
        _time = time;
        _lastPruneMs = NowMs();
    }

    public int Count => _entries.Count;

    public TValue? GetOrLoad(string path, Func<string, TValue?> load)
    {
        var now = NowMs();
        PruneIfDue(now);

        if (_entries.TryGetValue(path, out var entry)
            && (entry.Value is not null || now - entry.LoadedAtMs < (long)NegativeTtl.TotalMilliseconds))
        {
            Interlocked.Exchange(ref entry.LastSeenMs, now);
            return entry.Value;
        }

        var value = load(path);
        _entries[path] = new Entry(value, now);
        return value;
    }

    private void PruneIfDue(long now)
    {
        var last = Interlocked.Read(ref _lastPruneMs);
        if (now - last < PruneEveryMs || Interlocked.CompareExchange(ref _lastPruneMs, now, last) != last)
        {
            return;
        }

        var unseen = (long)UnseenTtl.TotalMilliseconds;
        foreach (var (key, entry) in _entries)
        {
            if (now - Interlocked.Read(ref entry.LastSeenMs) > unseen)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private long NowMs() => (long)_time.GetElapsedTime(0).TotalMilliseconds;

    private sealed class Entry(TValue? value, long loadedAtMs)
    {
        public long LastSeenMs = loadedAtMs;

        public TValue? Value { get; } = value;

        public long LoadedAtMs { get; } = loadedAtMs;
    }
}
