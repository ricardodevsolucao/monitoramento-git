using System.Buffers.Binary;
using System.Drawing;
using System.Reflection;

namespace Monitoramento.App.Hosting;

/// <summary>
/// Lê os quadros PNG do app.ico embutido e monta um <see cref="Icon"/> no tamanho pedido
/// (o decodificador do System.Drawing não lida bem com ICO de quadros PNG).
/// </summary>
internal static class AppIcon
{
    private static readonly Lazy<byte[]> Data = new(Load);

    private static byte[] Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico")
            ?? throw new InvalidOperationException("Ícone do aplicativo não encontrado nos recursos.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public static Icon Create(int preferredSize)
    {
        var data = Data.Value;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
        var bestSize = 0;
        var bestOffset = 0;
        var bestLength = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + (16 * i);
            var size = data[entry] == 0 ? 256 : data[entry];
            // menor quadro que ainda comporta o tamanho pedido; senão o maior disponível
            var better = bestSize == 0
                || (bestSize < preferredSize && size > bestSize)
                || (size >= preferredSize && size < bestSize);
            if (better)
            {
                bestSize = size;
                bestLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 8));
                bestOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 12));
            }
        }

        using var png = new MemoryStream(data, bestOffset, bestLength, writable: false);
        using var source = new Bitmap(png);
        using var scaled = source.Width == preferredSize ? new Bitmap(source) : new Bitmap(source, preferredSize, preferredSize);
        var handle = scaled.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            Native.Destroy(handle);
        }
    }
}
