using System.Collections.Concurrent;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Git;

internal interface IGitInfoResolver
{
    /// <summary>Repositório da pasta de trabalho; se ela for desconhecida, de um caminho absoluto citado na linha de comando.</summary>
    GitInfo? Resolve(string? workingDirectory, string? commandLine);

    /// <summary>
    /// Repositórios nas subpastas imediatas da pasta de trabalho, quando ela própria não está num repositório
    /// (workspace com vários repos). Vazio se a pasta está num repositório ou nada foi achado.
    /// </summary>
    IReadOnlyList<GitInfo> ResolveChildren(string? workingDirectory);
}

/// <summary>
/// Junta localização (cache 30 s por pasta), branch lida de HEAD (cache 2 s por repo) e o status em segundo plano.
/// </summary>
internal sealed class GitInfoResolver : IGitInfoResolver
{
    private const long LocationTtlMs = 30_000;
    private const long BranchTtlMs = 2_000;
    private const long ChildrenTtlMs = 60_000;
    private const int MaxCacheEntries = 4_096;

    private readonly GitStatusCache? _statusCache;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Cached<GitRepoLocation?>> _locations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Cached<string?>> _commandDirectories = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Cached<string?>> _branches = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Cached<string?>> _mainRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Cached<IReadOnlyList<GitRepoLocation>>> _children = new(StringComparer.OrdinalIgnoreCase);

    public GitInfoResolver(GitStatusCache? statusCache, TimeProvider? time = null)
    {
        _statusCache = statusCache;
        _time = time ?? TimeProvider.System;
    }

    public GitInfo? Resolve(string? workingDirectory, string? commandLine)
    {
        var directory = workingDirectory ?? DirectoryFromCommandLine(commandLine);
        if (directory is null)
        {
            return null;
        }

        var location = GetOrRefresh(_locations, directory, LocationTtlMs, GitRepositoryLocator.Locate);
        if (location is null)
        {
            return null;
        }

        return ToInfo(location);
    }

    public IReadOnlyList<GitInfo> ResolveChildren(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)
            || GetOrRefresh(_locations, workingDirectory, LocationTtlMs, GitRepositoryLocator.Locate) is not null)
        {
            return [];
        }

        // varredura de 1 nível: no máximo uma vez a cada 60 s por pasta (o Sample não repete I/O a cada ciclo)
        var children = GetOrRefresh(_children, workingDirectory, ChildrenTtlMs, GitRepositoryLocator.FindChildRepos);
        return children.Count == 0 ? [] : children.Select(ToInfo).ToList();
    }

    /// <summary>Branch lida de HEAD (cache 2 s) e o status do cache em segundo plano (mesmas regras de segurança).</summary>
    private GitInfo ToInfo(GitRepoLocation location)
    {
        var branch = GetOrRefresh(_branches, location.GitDir, BranchTtlMs, GitRepositoryLocator.ReadBranch);
        var status = _statusCache?.TryGet(location);
        var name = Path.GetFileName(location.RepoRoot);

        return new GitInfo(
            location.RepoRoot,
            string.IsNullOrEmpty(name) ? location.RepoRoot : name,
            branch,
            status?.Summary?.DirtyCount,
            status?.Summary?.Ahead,
            status?.Summary?.Behind,
            status?.Note,
            GetOrRefresh(_mainRoots, location.GitDir, LocationTtlMs, GitWorktrees.MainRepoRootFromGitDir));
    }

    private string? DirectoryFromCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        return GetOrRefresh(
            _commandDirectories,
            commandLine,
            LocationTtlMs,
            static line => CommandLinePaths.FindDirectory(line, Directory.Exists, File.Exists));
    }

    private T GetOrRefresh<T>(ConcurrentDictionary<string, Cached<T>> cache, string key, long ttlMs, Func<string, T> load)
    {
        var now = (long)_time.GetElapsedTime(0).TotalMilliseconds;
        if (cache.TryGetValue(key, out var cached) && now - cached.LoadedAtMs < ttlMs)
        {
            return cached.Value;
        }

        if (cache.Count >= MaxCacheEntries)
        {
            cache.Clear();
        }

        var value = load(key);
        cache[key] = new Cached<T>(value, now);
        return value;
    }

    private readonly record struct Cached<T>(T Value, long LoadedAtMs);
}
