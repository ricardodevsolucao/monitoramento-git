namespace Monitoramento.Core.Actions;

/// <summary>Processos que o app se recusa a encerrar.</summary>
internal static class ProcessProtection
{
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "wininit", "winlogon", "services", "lsass", "smss", "system", "registry", "dwm", "idle", "memory compression",
    };

    /// <returns>Mensagem de recusa em português, ou null se pode encerrar.</returns>
    public static string? CheckKill(int pid, string? name, int ownPid)
    {
        if (pid is 0 or 4)
        {
            return $"O processo {pid} é do sistema e não pode ser encerrado.";
        }

        if (pid == ownPid)
        {
            return "O próprio Monitoramento não pode ser encerrado por aqui.";
        }

        var baseName = BaseName(name);
        if (baseName is not null && Critical.Contains(baseName))
        {
            return $"\"{baseName}\" é um processo crítico do Windows e não pode ser encerrado.";
        }

        return null;
    }

    private static string? BaseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }
}
