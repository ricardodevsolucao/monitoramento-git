using Monitoramento.Core.Actions;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Git;

/// <summary>
/// Requisição <c>gitDetails</c>: só para repositórios que o motor detectou (<see cref="KnownGitRepos"/>), em caminho
/// local. Cache de 5 s por repositório (chamadas simultâneas compartilham a mesma leitura) e no máximo 2 leituras ao
/// mesmo tempo.
/// </summary>
internal sealed class GitDetailsService
{
    public const string UnknownRepoError = "Repositório desconhecido; abra-o a partir de um processo.";
    public const string NonLocalError = "Repositório inválido: o caminho precisa ser uma pasta local.";
    public const string NotARepoError = "A pasta não é (mais) um repositório git.";
    public const int DefaultMaxCommits = 150;
    public const int MaxCommitsLimit = 500;

    private const int MaxConcurrentReads = 2;
    private const long CacheTtlMs = 5_000;
    private const long PruneAfterMs = 60_000;

    private readonly IGitRunner _runner;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _reads = new(MaxConcurrentReads, MaxConcurrentReads);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public GitDetailsService(IGitRunner runner, KnownGitRepos known, TimeProvider? time = null)
    {
        _runner = runner;
        Known = known;
        _time = time ?? TimeProvider.System;
    }

    public KnownGitRepos Known { get; }

    /// <summary>Mudanças de um repositório pelo cache de status do snapshot (o motor liga); só usado para worktrees conhecidos.</summary>
    public Func<string, int?>? StatusDirtyCount { get; set; }

    public static int ClampMaxCommits(int? requested) => Math.Clamp(requested ?? DefaultMaxCommits, 1, MaxCommitsLimit);

    /// <summary>Valida o caminho vindo da UI: local e já detectado pelo motor. Devolve a forma normalizada.</summary>
    public OperationResult<string> ResolveRepo(string? repoRoot)
    {
        if (KnownGitRepos.Normalize(repoRoot) is not { } root)
        {
            return OperationResult<string>.Failure(NonLocalError);
        }

        return Known.Contains(root) ? OperationResult<string>.Success(root) : OperationResult<string>.Failure(UnknownRepoError);
    }

    /// <param name="force">Atualização manual: ignora o cache de 5 s.</param>
    public async Task<OperationResult<GitDetails>> GetAsync(string? repoRoot, int? maxCommits, bool force = false)
    {
        var resolved = ResolveRepo(repoRoot);
        if (!resolved.Ok)
        {
            return OperationResult<GitDetails>.Failure(resolved.Error!, resolved.Code);
        }

        if (force)
        {
            Invalidate(resolved.Value!);
        }

        return await GetValidatedAsync(resolved.Value!, ClampMaxCommits(maxCommits)).ConfigureAwait(false);
    }

    /// <summary>Descarta o cache do repositório (após uma ação) e lê de novo.</summary>
    public Task<OperationResult<GitDetails>> RefreshAsync(string root)
    {
        Invalidate(root);
        return GetValidatedAsync(root, DefaultMaxCommits);
    }

    public void Invalidate(string root)
    {
        var prefix = root + "|";
        lock (_gate)
        {
            foreach (var key in _cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _cache.Remove(key);
            }
        }
    }

    private async Task<OperationResult<GitDetails>> GetValidatedAsync(string root, int maxCommits)
    {
        var details = await GetOrStart(root, maxCommits).ConfigureAwait(false);
        return details is null ? OperationResult<GitDetails>.Failure(NotARepoError) : OperationResult<GitDetails>.Success(details);
    }

    private Task<GitDetails?> GetOrStart(string root, int maxCommits)
    {
        var key = root + "|" + maxCommits.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var now = NowMs();
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached) && cached.IsFresh(now))
            {
                return cached.Task;
            }

            Prune(now);
            var entry = new CacheEntry();
            entry.Task = LoadAsync(root, maxCommits, entry);
            _cache[key] = entry;
            return entry.Task;
        }
    }

    private async Task<GitDetails?> LoadAsync(string root, int maxCommits, CacheEntry entry)
    {
        await Task.Yield(); // nunca roda o git dentro do lock de quem pediu
        await _reads.WaitAsync().ConfigureAwait(false);
        try
        {
            var details = await GitDetailsReader.ReadAsync(_runner, root, maxCommits, dirtyOf: DirtyOfKnown).ConfigureAwait(false);

            // worktrees validados de um repositório conhecido passam a ser conhecidos (a UI pode abri-los numa aba)
            foreach (var worktree in details?.Worktrees ?? [])
            {
                if (!worktree.Prunable)
                {
                    Known.Register(worktree.Path);
                }
            }

            return details;
        }
        finally
        {
            _reads.Release();
            Volatile.Write(ref entry.CompletedAtMs, NowMs());
        }
    }

    private int? DirtyOfKnown(string path) =>
        Known.Contains(path) && StatusDirtyCount is { } dirty ? dirty(path) : null;

    private void Prune(long now)
    {
        foreach (var key in _cache.Where(kv => kv.Value.IsOlderThan(now, PruneAfterMs)).Select(kv => kv.Key).ToList())
        {
            _cache.Remove(key);
        }
    }

    private long NowMs() => (long)_time.GetElapsedTime(0).TotalMilliseconds;

    private sealed class CacheEntry
    {
        public const long Pending = -1;

        public long CompletedAtMs = Pending;

        public Task<GitDetails?> Task { get; set; } = null!;

        public bool IsFresh(long now)
        {
            var done = Volatile.Read(ref CompletedAtMs);
            return done == Pending || (now - done < CacheTtlMs && !Task.IsFaulted);
        }

        public bool IsOlderThan(long now, long ageMs)
        {
            var done = Volatile.Read(ref CompletedAtMs);
            return done != Pending && now - done > ageMs;
        }
    }
}
