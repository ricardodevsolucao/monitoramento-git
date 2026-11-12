using System.Diagnostics;
using Monitoramento.Core.Caching;
using Monitoramento.Core.Security;

namespace Monitoramento.Core.Grouping;

internal interface IFriendlyNameProvider
{
    string Resolve(string exeName, string? exePath);
}

/// <summary>
/// Nome amigável via FileVersionInfo: FileDescription → ProductName → exe sem extensão. Cache por caminho (sem nome:
/// tenta de novo após 1 min; executável não visto há 10 min sai do cache). Só lê caminho local.
/// </summary>
internal sealed class FileVersionNameProvider : IFriendlyNameProvider
{
    private readonly PathValueCache<string> _byPath;
    private readonly Func<string, string?> _read;

    public FileVersionNameProvider()
        : this(TimeProvider.System, ReadVersionName)
    {
    }

    internal FileVersionNameProvider(TimeProvider time, Func<string, string?> read)
    {
        _byPath = new PathValueCache<string>(time);
        _read = read;
    }

    /// <summary>Executáveis em cache (para testes de poda).</summary>
    internal int Count => _byPath.Count;

    public string Resolve(string exeName, string? exePath)
    {
        var fallback = Path.GetFileNameWithoutExtension(exeName);
        if (string.IsNullOrEmpty(fallback))
        {
            fallback = exeName;
        }

        if (!LocalPath.TryGetLocal(exePath, out var path))
        {
            return fallback;
        }

        return _byPath.GetOrLoad(path, _read) ?? fallback;
    }

    private static string? ReadVersionName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return FirstNonBlank(info.FileDescription, info.ProductName);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
