namespace Monitoramento.Core.Collection;

/// <summary>
/// Cache de <see cref="ProcessDetails"/> por (pid, createTime). Caminho e linha de comando são lidos uma vez;
/// a pasta de trabalho é relida no máximo a cada <see cref="WorkingDirectoryRefreshMs"/>. Processos sem acesso
/// (OpenProcess falhou) não são consultados de novo enquanto viverem. Leitura incompleta (processo recém-criado,
/// ainda sem ProcessParameters) é refeita com espera crescente, até <see cref="MaxReadAttempts"/> leituras; esgotadas,
/// o processo passa a contar como sem acesso.
/// </summary>
internal sealed class CachedProcessDetailsProvider : IProcessDetailsProvider
{
    public const long WorkingDirectoryRefreshMs = 10_000;
    public const int MaxReadAttempts = 5;
    private const long FirstRetryDelayMs = 250;

    private readonly IProcessReader _reader;
    private readonly Func<long> _nowMs;
    private readonly Dictionary<(int Pid, long CreateTime), Entry> _cache = [];

    public CachedProcessDetailsProvider(IProcessReader reader, Func<long> nowMs)
    {
        _reader = reader;
        _nowMs = nowMs;
    }

    public ProcessDetails Get(in RawProcess process)
    {
        var key = (process.Pid, process.CreateTime);
        var now = _nowMs();

        if (!_cache.TryGetValue(key, out var entry))
        {
            entry = new Entry(now);
            entry.Accept(_reader.Read(process.Pid), now);
            _cache[key] = entry;
            return entry.Details;
        }

        if (entry.Details.Incomplete)
        {
            if (now >= entry.NextRetryAt)
            {
                entry.Accept(_reader.Read(process.Pid), now);
            }

            return entry.Details;
        }

        if (!entry.Details.AccessDenied && now - entry.WorkingDirectoryReadAt >= WorkingDirectoryRefreshMs)
        {
            var cwd = _reader.ReadWorkingDirectory(process.Pid);
            entry.WorkingDirectoryReadAt = now;
            if (cwd is not null && !string.Equals(cwd, entry.Details.WorkingDirectory, StringComparison.Ordinal))
            {
                entry.Details = entry.Details with { WorkingDirectory = cwd };
            }
        }

        return entry.Details;
    }

    public void Prune(IReadOnlyList<RawProcess> alive)
    {
        if (_cache.Count == 0)
        {
            return;
        }

        var keep = new HashSet<(int, long)>(alive.Count);
        foreach (var p in alive)
        {
            keep.Add((p.Pid, p.CreateTime));
        }

        foreach (var key in _cache.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _cache.Remove(key);
        }
    }

    private sealed class Entry(long readAt)
    {
        public ProcessDetails Details { get; set; } = new(null, null, null, AccessDenied: true);

        public long WorkingDirectoryReadAt { get; set; } = readAt;

        public long NextRetryAt { get; private set; }

        private int Attempts { get; set; }

        public void Accept(ProcessDetails details, long now)
        {
            Attempts++;
            WorkingDirectoryReadAt = now;
            if (details.Incomplete && Attempts >= MaxReadAttempts)
            {
                details = details with { AccessDenied = true, Incomplete = false }; // desiste: conta como sem acesso
            }

            Details = details;
            NextRetryAt = now + (FirstRetryDelayMs << (Attempts - 1));
        }
    }
}
