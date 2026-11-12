using Monitoramento.Core.Actions;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Launcher;

/// <summary>
/// Monta o início de um processo salvo. O comando do usuário roda via cmd por definição (é uma linha de shell);
/// a pasta e as variáveis vão por campos próprios, nunca concatenadas. Não há cmd intermediário: o processo criado
/// é o próprio cmd que executa o comando, e é o pid dele que fica rastreado.
/// </summary>
internal static class LaunchCommandBuilder
{
    /// <remarks>
    /// "hidden": <c>cmd.exe /s /c "&lt;comando&gt;"</c> sem janela.
    /// "window": <c>cmd.exe /s /k "&lt;comando&gt;"</c> num console novo (CREATE_NEW_CONSOLE) com o nome como título.
    /// "terminal": a mesma linha do "window", rodada num terminal embutido (ConPTY); sem ConPTY, cai para "window".
    /// </remarks>
    public static LaunchSpec Build(SavedProcess saved, string systemDirectory)
    {
        var cmd = Path.Combine(systemDirectory, "cmd.exe");
        var command = saved.Command.Trim();
        var hidden = LaunchModes.Normalize(saved.LaunchMode) == LaunchModes.Hidden;
        var mode = hidden ? "/c" : "/k";

        return new LaunchSpec(
            cmd,
            $"\"{cmd}\" /s {mode} \"{command}\"",
            string.IsNullOrWhiteSpace(saved.WorkingDirectory) ? null : saved.WorkingDirectory,
            Environment(saved.Env),
            !hidden,
            Title(saved));
    }

    /// <summary>Chaves repetidas (sem diferenciar maiúsculas, como no Windows): a última vence.</summary>
    private static Dictionary<string, string> Environment(IReadOnlyDictionary<string, string> env)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in env)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key.Trim()] = value ?? string.Empty;
            }
        }

        return result;
    }

    /// <summary>Título da janela sem aspas nem caracteres de controle.</summary>
    private static string Title(SavedProcess saved)
    {
        var raw = string.IsNullOrWhiteSpace(saved.Name) ? "Monitoramento" : saved.Name;
        var clean = new string(raw.Where(c => c is not ('"' or '&' or '|' or '<' or '>' or '^' or '%') && !char.IsControl(c)).ToArray());
        return string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
