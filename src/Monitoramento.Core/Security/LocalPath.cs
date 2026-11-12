using System.Collections.Concurrent;

namespace Monitoramento.Core.Security;

/// <summary>
/// Validador único de caminho local. Todo caminho vindo de dado não confiável (pasta de trabalho lida do PEB,
/// linha de comando, arquivo ".git" com "gitdir:", mensagens da UI) passa por aqui antes de qualquer acesso
/// ao sistema de arquivos, para não disparar autenticação SMB nem abrir dispositivos.
/// </summary>
/// <remarks>
/// Recusa: UNC (<c>\\host</c>, <c>//host</c>), prefixos de dispositivo (<c>\\?\</c>, <c>\\.\</c>, <c>\??\</c>),
/// <c>GLOBALROOT</c>, nomes reservados (CON, NUL, COM1…), caminhos relativos ou sem unidade e unidades que não sejam
/// fixas ou removíveis (rede, CD, RAM disk, inexistente).
/// </remarks>
internal static class LocalPath
{
    private static readonly long DriveTypeTtlMs = 60_000;
    private static readonly ConcurrentDictionary<char, (DriveType Type, long ReadAtMs)> DriveTypes = new();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>Verdadeiro se <paramref name="path"/> é um caminho local seguro (unidade fixa ou removível).</summary>
    public static bool IsLocal(string? path) => TryGetLocal(path, out _);

    /// <summary>Valida e normaliza (<see cref="Path.GetFullPath(string)"/>) um caminho local.</summary>
    public static bool TryGetLocal(string? path, out string fullPath) => TryGetLocal(path, CachedDriveType, out fullPath);

    /// <param name="driveTypeOf">Tipo da unidade a partir da raiz ("C:\"); injetável para testes.</param>
    internal static bool TryGetLocal(string? path, Func<string, DriveType> driveTypeOf, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !IsSyntacticallyLocal(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }

        // a normalização não pode ter produzido um caminho de rede/dispositivo
        if (!IsSyntacticallyLocal(full) || HasReservedSegment(full))
        {
            return false;
        }

        var root = Path.GetPathRoot(full);
        if (root is null || root.Length != 3)
        {
            return false;
        }

        if (driveTypeOf(root) is not (DriveType.Fixed or DriveType.Removable))
        {
            return false;
        }

        fullPath = Canonical(full);
        return true;
    }

    /// <summary>
    /// Forma canônica de um caminho já validado: letra de unidade maiúscula e, se houver segmento 8.3 ("RICARD~1.MAR")
    /// e o caminho existir, o nome longo — para o mesmo repositório não aparecer com duas grafias.
    /// </summary>
    private static string Canonical(string full)
    {
        var path = char.IsAsciiLetterLower(full[0]) ? char.ToUpperInvariant(full[0]) + full[1..] : full;
        if (!path.Contains('~', StringComparison.Ordinal))
        {
            return path;
        }

        var longPath = Interop.FileSystemNative.LongPathName(path);
        if (longPath is null || !IsSyntacticallyLocal(longPath) || !string.Equals(Path.GetPathRoot(longPath), Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return char.IsAsciiLetterLower(longPath[0]) ? char.ToUpperInvariant(longPath[0]) + longPath[1..] : longPath;
    }

    /// <summary>Só a forma: "X:\…" ou "X:/…", sem caracteres inválidos nem GLOBALROOT.</summary>
    private static bool IsSyntacticallyLocal(string path)
    {
        if (path.Length < 3
            || !char.IsAsciiLetter(path[0])
            || path[1] != ':'
            || path[2] is not ('\\' or '/')
            || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || path.IndexOf(':', 2) >= 0
            || path.Contains("GLOBALROOT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool HasReservedSegment(string full)
    {
        foreach (var segment in full[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            var dot = segment.IndexOf('.', StringComparison.Ordinal);
            var stem = (dot >= 0 ? segment[..dot] : segment).TrimEnd(' ');
            if (ReservedNames.Contains(stem))
            {
                return true;
            }
        }

        return false;
    }

    private static DriveType CachedDriveType(string root)
    {
        var letter = char.ToUpperInvariant(root[0]);
        var now = Environment.TickCount64;
        if (DriveTypes.TryGetValue(letter, out var cached) && now - cached.ReadAtMs < DriveTypeTtlMs)
        {
            return cached.Type;
        }

        DriveType type;
        try
        {
            type = new DriveInfo(root).DriveType;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            type = DriveType.Unknown;
        }

        DriveTypes[letter] = (type, now);
        return type;
    }
}
