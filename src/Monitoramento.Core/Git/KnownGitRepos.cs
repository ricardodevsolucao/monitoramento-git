using System.Collections.Concurrent;
using System.Collections.Frozen;
using Monitoramento.Core.Security;

namespace Monitoramento.Core.Git;

/// <summary>
/// Repositórios que o motor detectou sozinho (processos e salvos dos últimos snapshots) ou fixou a partir da
/// configuração (projetos git salvos e abas do workspace, já validados). A visão Git e as ações só aceitam esses: a UI
/// não consegue apontar o git para um caminho arbitrário. Os vistos expiram após 30 min; os fixados, não.
/// </summary>
internal sealed class KnownGitRepos(TimeProvider? time = null)
{
    public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(30);

    internal const int MaxEntries = 2_048;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, long> _seenAtMs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _registerGate = new();
    private volatile FrozenSet<string> _pinned = FrozenSet<string>.Empty;
    private readonly ConcurrentDictionary<string, byte> _extraPinned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Caminho local completo e canônico (unidade maiúscula, nome longo no lugar do 8.3), sem separador final (exceto
    /// na raiz "C:\"); null se não for local. É a forma usada para gravar e comparar.
    /// </summary>
    public static string? Normalize(string? path) =>
        LocalPath.TryGetLocal(path, out var full) ? FolderPath.Trim(full) : null;

    /// <summary>Quantos repositórios vistos (não fixados) estão guardados; nunca passa de <see cref="MaxEntries"/>.</summary>
    internal int SeenCount => _seenAtMs.Count;

    public void Register(string? repoRoot)
    {
        if (Normalize(repoRoot) is not { } key)
        {
            return;
        }

        lock (_registerGate)
        {
            var now = NowMs();
            if (_seenAtMs.Count >= MaxEntries && !_seenAtMs.ContainsKey(key))
            {
                Prune(now);
                if (_seenAtMs.Count >= MaxEntries)
                {
                    // teto estrito: cheio só de entradas frescas → sai a vista há mais tempo
                    var oldest = _seenAtMs.MinBy(entry => entry.Value).Key;
                    _seenAtMs.TryRemove(oldest, out _);
                }
            }

            _seenAtMs[key] = now;
        }
    }

    /// <param name="normalizedRoot">Resultado de <see cref="Normalize"/>.</param>
    public bool Contains(string normalizedRoot) =>
        _pinned.Contains(normalizedRoot) || _extraPinned.ContainsKey(normalizedRoot) || (_seenAtMs.TryGetValue(normalizedRoot, out var seen) && IsFresh(seen, NowMs()));

    /// <summary>Troca o conjunto fixado (não expira). Quem chama já validou cada raiz (local e repositório de verdade).</summary>
    /// <param name="normalizedRoots">Resultados de <see cref="Normalize"/>.</param>
    public void SetPinned(IEnumerable<string> normalizedRoots) =>
        _pinned = normalizedRoots.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fixa um repositório fora da configuração (ex.: worktree criado pelo app), até <see cref="Unpin"/>.</summary>
    public void Pin(string normalizedRoot) => _extraPinned[normalizedRoot] = 0;

    public void Unpin(string normalizedRoot)
    {
        _extraPinned.TryRemove(normalizedRoot, out _);
        _seenAtMs.TryRemove(normalizedRoot, out _);
    }

    /// <summary>Verdadeiro se a pasta é a mãe direta de algum repositório conhecido (fixado ou visto há menos de 30 min).</summary>
    /// <param name="normalizedFolder">Resultado de <see cref="Normalize"/>.</param>
    public bool IsParentOfKnown(string normalizedFolder)
    {
        bool IsChild(string root) => FolderPath.IsParentOf(normalizedFolder, root);

        if (_pinned.Any(IsChild) || _extraPinned.Keys.Any(IsChild))
        {
            return true;
        }

        var now = NowMs();
        return _seenAtMs.Any(entry => IsFresh(entry.Value, now) && IsChild(entry.Key));
    }

    private static bool IsFresh(long seenAtMs, long nowMs) => nowMs - seenAtMs <= (long)Expiry.TotalMilliseconds;

    private void Prune(long now)
    {
        foreach (var (key, seen) in _seenAtMs)
        {
            if (!IsFresh(seen, now))
            {
                _seenAtMs.TryRemove(key, out _);
            }
        }
    }

    private long NowMs() => (long)_time.GetElapsedTime(0).TotalMilliseconds;
}
