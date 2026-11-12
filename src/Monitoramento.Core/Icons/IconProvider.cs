using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Monitoramento.Core.Caching;
using Monitoramento.Core.Security;

namespace Monitoramento.Core.Icons;

/// <summary>
/// Ícone associado a um executável, em PNG, com cache por caminho (sem ícone: tenta de novo após 1 min; executável
/// não pedido há 10 min sai do cache). Só aceita caminho local (<see cref="LocalPath"/>) de um .exe existente, para
/// não acionar rede nem outros handlers.
/// </summary>
public sealed class IconProvider
{
    private const string DataUrlPrefix = "data:image/png;base64,";

    private readonly PathValueCache<byte[]> _cache;
    private readonly Func<string, byte[]?> _extract;

    public IconProvider()
        : this(TimeProvider.System, Extract)
    {
    }

    internal IconProvider(TimeProvider time, Func<string, byte[]?> extract)
    {
        _cache = new PathValueCache<byte[]>(time);
        _extract = extract;
    }

    /// <summary>Executáveis em cache (para testes de poda).</summary>
    internal int Count => _cache.Count;

    /// <returns>Bytes PNG do ícone, ou null se o caminho for inválido ou não tiver ícone.</returns>
    public byte[]? GetPng(string? exePath)
    {
        var path = Validate(exePath);
        return path is null ? null : _cache.GetOrLoad(path, _extract);
    }

    /// <summary>Data URL ("data:image/png;base64,...") para a resposta de getIcons; null quando não há ícone.</summary>
    public string? GetDataUrl(string? iconKey) =>
        GetPng(iconKey) is { } png ? DataUrlPrefix + Convert.ToBase64String(png) : null;

    private static string? Validate(string? exePath)
    {
        if (!LocalPath.TryGetLocal(exePath, out var full) || !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(full) ? full : null;
    }

    private static byte[]? Extract(string path)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }

            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or ExternalException or Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
