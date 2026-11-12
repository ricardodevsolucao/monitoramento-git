using Monitoramento.Core.Security;

namespace Monitoramento.Core.Terminal;

/// <summary>Shell padrão do <c>termOpen</c>: PowerShell 7 (<c>pwsh.exe</c>) se estiver no PATH, senão o Windows PowerShell.</summary>
internal static class ShellLocator
{
    private static readonly IReadOnlyDictionary<string, string> NoExtraVariables = new Dictionary<string, string>();

    public static TerminalLaunch Find() => Find(Environment.GetEnvironmentVariable("PATH"), Environment.SystemDirectory, File.Exists);

    /// <param name="path">Conteúdo do PATH; só pastas locais (LocalPath) são consideradas.</param>
    internal static TerminalLaunch Find(string? path, string systemDirectory, Func<string, bool> exists)
    {
        foreach (var folder in (path ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (LocalPath.TryGetLocal(folder.Trim('"'), out var local))
            {
                var pwsh = Path.Combine(local, "pwsh.exe");
                if (exists(pwsh))
                {
                    return new TerminalLaunch(pwsh, $"\"{pwsh}\" -NoLogo", NoExtraVariables);
                }
            }
        }

        var powershell = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return new TerminalLaunch(powershell, $"\"{powershell}\" -NoLogo", NoExtraVariables);
    }
}
