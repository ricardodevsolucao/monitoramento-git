namespace Monitoramento.Core.Launcher;

/// <summary>Leitura de linhas de comando como o cmd.exe as interpreta.</summary>
internal static class ShellText
{
    /// <summary>
    /// Verdadeiro se há <c>&amp; | &lt; &gt; ^ %</c> fora de aspas duplas: copiada como linha de shell, a linha
    /// de comando de outro processo executaria esses trechos como comandos/redirecionamentos.
    /// </summary>
    public static bool HasUnquotedMetacharacters(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
        {
            return false;
        }

        var inQuotes = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && c is '&' or '|' or '<' or '>' or '^' or '%')
            {
                return true;
            }
        }

        return false;
    }
}
