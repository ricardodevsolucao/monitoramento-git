namespace Monitoramento.Core.Git;

/// <summary>Comparação e forma de pasta sem tocar o disco (único helper para "mesma pasta").</summary>
internal static class FolderPath
{
    /// <summary>Apara espaços e separadores finais (exceto na raiz "C:\") e põe a letra de unidade em maiúscula.</summary>
    public static string Trim(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.Length > 3)
        {
            trimmed = trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return trimmed.Length >= 2 && trimmed[1] == ':' && char.IsAsciiLetterLower(trimmed[0])
            ? char.ToUpperInvariant(trimmed[0]) + trimmed[1..]
            : trimmed;
    }

    /// <summary>Mesma pasta, sem diferenciar maiúsculas nem separador final.</summary>
    public static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Verdadeiro se <paramref name="folder"/> é a mãe direta de <paramref name="path"/>.</summary>
    public static bool IsParentOf(string folder, string path) => Same(Path.GetDirectoryName(Trim(path)), folder);
}

/// <summary>
/// Pastas amplas demais para virar projeto ou aba: raiz de unidade, a pasta do usuário e as conhecidas dentro dela
/// (Área de Trabalho, Documentos, Downloads, raiz do OneDrive) e as pastas do sistema (Windows, Arquivos de Programas,
/// ProgramData, Usuários). Abrir uma delas varreria/listaria repositórios demais.
/// </summary>
internal static class BroadFolders
{
    private static readonly Lazy<HashSet<string>> Known = new(Build);

    /// <param name="folder">Pasta já normalizada (<see cref="KnownGitRepos.Normalize"/>) ou aparada.</param>
    public static bool IsBroad(string folder)
    {
        var trimmed = FolderPath.Trim(folder);
        return trimmed.Length <= 3 || Known.Value.Contains(trimmed);
    }

    private static HashSet<string> Build()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string?[] candidates =
        [
            profile,
            Path.GetDirectoryName(profile), // C:\Users
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            profile.Length == 0 ? null : Path.Combine(profile, "Downloads"),
            Environment.GetEnvironmentVariable("OneDrive"),
            Environment.GetEnvironmentVariable("OneDriveConsumer"),
            Environment.GetEnvironmentVariable("OneDriveCommercial"),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ];

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                set.Add(KnownGitRepos.Normalize(candidate) ?? FolderPath.Trim(candidate));
            }
        }

        return set;
    }
}
