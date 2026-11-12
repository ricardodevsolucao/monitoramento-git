using System.Text;

namespace Monitoramento.Core.Launcher;

/// <summary>Normalização de linha de comando: minúsculas, espaços colapsados, sem bordas.</summary>
internal static class CommandText
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text.AsSpan().Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>
    /// <paramref name="needle"/> aparece em <paramref name="haystack"/> em fronteira de token: antes vem o início, espaço
    /// ou aspas; depois, o fim, espaço ou aspas. Assim "npm run dev" não casa com "npm run dev:api".
    /// </summary>
    public static bool ContainsToken(string haystack, string needle)
    {
        if (needle.Length == 0)
        {
            return false;
        }

        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = haystack.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            var end = at + needle.Length;
            if ((at == 0 || IsBoundary(haystack[at - 1])) && (end == haystack.Length || IsBoundary(haystack[end])))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBoundary(char c) => c is ' ' or '"' or '\'';
}

/// <summary>Comparação de caminhos do Windows: sem diferenciar maiúsculas e ignorando separador final.</summary>
internal static class PathText
{
    public static bool AreEqual(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        return Trim(a).Equals(Trim(b), StringComparison.OrdinalIgnoreCase);
    }

    public static ReadOnlySpan<char> Trim(string path)
    {
        var span = path.AsSpan().Trim();
        while (span.Length > 1 && (span[^1] == '\\' || span[^1] == '/'))
        {
            span = span[..^1];
        }

        return span;
    }
}
