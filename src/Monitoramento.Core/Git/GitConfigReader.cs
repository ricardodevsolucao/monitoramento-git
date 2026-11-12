using System.Collections.Concurrent;

namespace Monitoramento.Core.Git;

/// <summary>Chave da config como o git a entende (seção e nome em minúsculas; subseção preservada) e o valor (null = chave sem "=").</summary>
internal sealed record GitConfigEntry(string Key, string? Value);

/// <summary>
/// Lê um arquivo de config com o parser do próprio git (<c>git config --file … --no-includes --null --list</c>), que só
/// interpreta a sintaxe e não executa nada. Roda isolado: sem <c>-C</c>, numa pasta neutra, sem config global/sistema e
/// sem as variáveis GIT_* herdadas. Resultado em cache por (caminho, tamanho, data de modificação).
/// </summary>
internal sealed class GitConfigReader(string? gitExe)
{
    private const long MaxFileBytes = 1024 * 1024;
    private const int MaxCacheEntries = 512;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<string, (long Length, long WriteTicks, IReadOnlyList<GitConfigEntry>? Entries)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private int _processCount;

    public static GitConfigReader Default { get; } = new(GitExecutable.Find());

    /// <summary>Quantas vezes o git foi executado (para testar o cache).</summary>
    public int ProcessCount => Volatile.Read(ref _processCount);

    /// <summary>Pasta vazia usada como diretório de trabalho do git isolado.</summary>
    internal static string NeutralDirectory
    {
        get
        {
            var path = Path.Combine(Path.GetTempPath(), "Monitoramento", "git-neutro");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <returns>Entradas do arquivo; vazio se ele não existir; null se for inválido, ilegível, grande demais ou um link.</returns>
    public async Task<IReadOnlyList<GitConfigEntry>?> ReadAsync(string file)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(file);
            if (!info.Exists)
            {
                return [];
            }

            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaxFileBytes)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        var writeTicks = info.LastWriteTimeUtc.Ticks;
        if (_cache.TryGetValue(info.FullName, out var cached) && cached.Length == info.Length && cached.WriteTicks == writeTicks)
        {
            return cached.Entries;
        }

        var entries = await ParseWithGitAsync(info.FullName).ConfigureAwait(false);
        if (_cache.Count >= MaxCacheEntries)
        {
            _cache.Clear();
        }

        _cache[info.FullName] = (info.Length, writeTicks, entries);
        return entries;
    }

    private async Task<IReadOnlyList<GitConfigEntry>?> ParseWithGitAsync(string file)
    {
        if (gitExe is null)
        {
            return null;
        }

        Interlocked.Increment(ref _processCount);
        var request = new GitRunRequest(
            NeutralDirectory,
            ["config", "--file", file, "--no-includes", "--null", "--list"],
            GitRunMode.Isolated,
            Timeout,
            (int)MaxFileBytes * 2);
        var result = await new GitProcessRunner(gitExe).RunAsync(request).ConfigureAwait(false);
        return result.Ok ? Parse(result.Output) : null; // config inválida: o git sai com erro (e não roda nada)
    }

    /// <summary>Com <c>--null</c>: entradas separadas por NUL; chave e valor separados por quebra de linha.</summary>
    internal static IReadOnlyList<GitConfigEntry> Parse(string output)
    {
        var entries = new List<GitConfigEntry>();
        foreach (var item in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var newline = item.IndexOf('\n', StringComparison.Ordinal);
            var key = (newline >= 0 ? item[..newline] : item).ToLowerInvariant();
            entries.Add(new GitConfigEntry(key, newline >= 0 ? item[(newline + 1)..] : null));
        }

        return entries;
    }
}
