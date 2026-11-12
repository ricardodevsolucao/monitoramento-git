using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Monitoramento.Core.Security;

namespace Monitoramento.Core.Git;

/// <summary>Leitura do status: resumo (ou null em falha) e, quando o status foi pulado por segurança, o motivo.</summary>
internal sealed record GitStatusReading(GitStatusSummary? Summary, string? Note);

/// <summary>
/// <c>git status --porcelain=v2 --branch</c> em segundo plano, com cache por repositório.
/// <see cref="TryGet"/> nunca bloqueia: devolve a última leitura (ou null) e agenda a atualização quando vencida.
/// O git só roda em caminho local e quando <see cref="GitRepoSafety"/> aprova a config do repositório, sempre com
/// fsmonitor, hooks e untracked cache desligados e sem prompts.
/// </summary>
internal sealed class GitStatusCache : IDisposable
{
    private const int MaxConcurrentGit = 2;
    private const long NeverFetched = long.MinValue;
    private static readonly long UnseenTtlMs = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
    private static readonly long PruneEveryMs = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;

    private readonly string _gitExe;
    private readonly long _ttlMs;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentGit, MaxConcurrentGit);
    private readonly CancellationTokenSource _disposed = new();
    private long _lastPruneMs;

    public GitStatusCache(string gitExe, TimeSpan ttl, TimeSpan timeout, TimeProvider? time = null)
    {
        _gitExe = gitExe;
        _ttlMs = (long)ttl.TotalMilliseconds;
        _timeout = timeout;
        _time = time ?? TimeProvider.System;
        _lastPruneMs = NowMs();
    }

    /// <summary>Repositórios em cache (para testes de poda).</summary>
    internal int Count => _entries.Count;

    public GitStatusReading? TryGet(GitRepoLocation location)
    {
        if (_disposed.IsCancellationRequested)
        {
            return null;
        }

        var now = NowMs();
        PruneIfDue(now);

        var entry = _entries.GetOrAdd(location.RepoRoot, static _ => new Entry());
        Interlocked.Exchange(ref entry.LastSeenMs, now);
        var fetched = Interlocked.Read(ref entry.FetchedAtMs);
        var stale = fetched == NeverFetched || now - fetched >= _ttlMs;
        if (stale && Interlocked.CompareExchange(ref entry.InFlight, 1, 0) == 0)
        {
            _ = Task.Run(() => RefreshAsync(location, entry));
        }

        return entry.Value;
    }

    public void Dispose()
    {
        _disposed.Cancel();
        _disposed.Dispose();
    }

    /// <summary>Linha de comando do status com tudo que pode executar código desligado.</summary>
    internal static ProcessStartInfo BuildStartInfo(string gitExe, string repoRoot)
    {
        var psi = new ProcessStartInfo(gitExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };
        string[] args =
        [
            "-c", "core.fsmonitor=false",
            "-c", "core.hooksPath=NUL",
            "-c", "core.untrackedCache=false",
            "--no-optional-locks",
            "-C", repoRoot,
            "status", "--porcelain=v2", "--branch", "--ignore-submodules=all",
        ];
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        GitProcessRunner.RemoveInheritedGitVariables(psi);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        return psi;
    }

    private void PruneIfDue(long now)
    {
        var last = Interlocked.Read(ref _lastPruneMs);
        if (now - last < PruneEveryMs || Interlocked.CompareExchange(ref _lastPruneMs, now, last) != last)
        {
            return;
        }

        foreach (var (key, entry) in _entries)
        {
            if (now - Interlocked.Read(ref entry.LastSeenMs) > UnseenTtlMs)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private async Task RefreshAsync(GitRepoLocation location, Entry entry)
    {
        var acquired = false;
        GitStatusReading? reading = null;
        try
        {
            await _concurrency.WaitAsync(_disposed.Token).ConfigureAwait(false);
            acquired = true;
            reading = await ReadAsync(location).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // encerrando
        }
        catch (ObjectDisposedException)
        {
            // encerrando
        }
        catch (Exception ex)
        {
            // segundo plano: nenhuma falha pode derrubar o processo; o valor antigo não é mantido
            Trace.TraceWarning($"Monitoramento: falha ao ler o status git de \"{location.RepoRoot}\": {ex.GetType().Name}");
        }
        finally
        {
            if (acquired)
            {
                _concurrency.Release();
            }

            entry.Value = reading; // falha/timeout → null: não mantém dirty/ahead/behind velhos
            Interlocked.Exchange(ref entry.FetchedAtMs, NowMs());
            Volatile.Write(ref entry.InFlight, 0);
        }
    }

    private async Task<GitStatusReading?> ReadAsync(GitRepoLocation location)
    {
        if (!LocalPath.IsLocal(location.RepoRoot) || !LocalPath.IsLocal(location.GitDir))
        {
            return new GitStatusReading(null, GitRepoSafety.NonLocalNote);
        }

        var note = await GitRepoSafety.CheckAsync(location).ConfigureAwait(false);
        if (note is not null)
        {
            return new GitStatusReading(null, note);
        }

        var summary = await RunStatusAsync(location.RepoRoot).ConfigureAwait(false);
        return summary is null ? null : new GitStatusReading(summary, null);
    }

    private async Task<GitStatusSummary?> RunStatusAsync(string repoRoot)
    {
        Process? process = null;
        try
        {
            process = Process.Start(BuildStartInfo(_gitExe, repoRoot));
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposed.Token);
            timeout.CancelAfter(_timeout);

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);

            return process.ExitCode == 0 ? PorcelainV2Parser.Parse(output) : null;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            process?.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // já terminou
        }
    }

    private long NowMs() => (long)_time.GetElapsedTime(0).TotalMilliseconds;

    private sealed class Entry
    {
        public int InFlight;

        public long FetchedAtMs = NeverFetched;

        public long LastSeenMs;

        public volatile GitStatusReading? Value;
    }
}
