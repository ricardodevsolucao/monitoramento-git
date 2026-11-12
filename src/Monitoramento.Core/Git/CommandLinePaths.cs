using Monitoramento.Core.Security;

namespace Monitoramento.Core.Git;

/// <summary>Extrai de uma linha de comando uma pasta existente a partir de caminhos absolutos citados nela.</summary>
internal static class CommandLinePaths
{
    /// <summary>
    /// Procura primeiro nos argumentos e por último no próprio executável. Arquivo → pasta que o contém.
    /// Só consulta o disco para caminhos locais (<paramref name="isLocal"/>, padrão <see cref="LocalPath.IsLocal"/>).
    /// </summary>
    public static string? FindDirectory(
        string? commandLine,
        Func<string, bool> directoryExists,
        Func<string, bool> fileExists,
        Func<string, bool>? isLocal = null)
    {
        isLocal ??= LocalPath.IsLocal;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var tokens = Tokenize(commandLine);
        for (var i = 1; i <= tokens.Count; i++)
        {
            var token = tokens[i % tokens.Count]; // 1..n-1 e por fim 0
            var found = TryDirectory(token, directoryExists, fileExists, isLocal);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string? TryDirectory(string token, Func<string, bool> directoryExists, Func<string, bool> fileExists, Func<string, bool> isLocal)
    {
        var equals = token.IndexOf('=', StringComparison.Ordinal);
        var candidate = equals >= 0 && token.StartsWith('-') ? token[(equals + 1)..] : token;
        candidate = candidate.Trim('"', '\'');

        if (candidate.Length < 3 || !IsFullyQualified(candidate) || !isLocal(candidate))
        {
            return null;
        }

        if (directoryExists(candidate))
        {
            return candidate.Length > 3 ? candidate.TrimEnd('\\', '/') : candidate;
        }

        return fileExists(candidate) ? Path.GetDirectoryName(candidate) : null;
    }

    private static bool IsFullyQualified(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Separa por espaços respeitando aspas duplas (regras simplificadas do Windows).</summary>
    private static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
