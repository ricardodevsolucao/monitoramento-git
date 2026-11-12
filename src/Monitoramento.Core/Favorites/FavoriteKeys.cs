using System.Security.Cryptography;
using System.Text;
using Monitoramento.Core.Launcher;

namespace Monitoramento.Core.Favorites;

/// <summary>
/// Chaves de favorito: "app:&lt;exe minúsculo&gt;" para grupos e "cmd:&lt;12 hex&gt;" para uma linha de comando.
/// O hash é SHA-256 (estável entre execuções) de "exe\ncomando", ambos normalizados.
/// </summary>
internal static class FavoriteKeys
{
    public const string AppPrefix = "app:";
    public const string CommandPrefix = "cmd:";
    private const int HashChars = 12;

    public static string ForApp(string exeName) => AppPrefix + exeName.Trim().ToLowerInvariant();

    public static string ForCommand(string exeName, string? commandLine)
    {
        var text = CommandText.Normalize(exeName) + "\n" + CommandText.Normalize(commandLine);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        return CommandPrefix + Convert.ToHexStringLower(hash)[..HashChars];
    }

    /// <summary>
    /// Chave de favorito de um processo: "cmd:" do exe + linha de comando; sem linha de comando (sem acesso),
    /// todas as instâncias colidiriam no mesmo hash, então usa a chave do grupo ("app:").
    /// </summary>
    public static string ForProcess(string exeName, string? commandLine) =>
        string.IsNullOrWhiteSpace(commandLine) ? ForApp(exeName) : ForCommand(exeName, commandLine);

    public static bool IsValid(string? key) =>
        key is not null
        && key.Length <= 300
        && ((key.StartsWith(AppPrefix, StringComparison.Ordinal) && key.Length > AppPrefix.Length)
            || (key.StartsWith(CommandPrefix, StringComparison.Ordinal) && key.Length > CommandPrefix.Length));
}
