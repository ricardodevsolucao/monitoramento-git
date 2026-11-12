using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Monitoramento.Core.Launcher;
using Monitoramento.Core.Models;
using Monitoramento.Core.Security;

namespace Monitoramento.Core.Actions;

/// <summary>
/// Ações pedidas pela UI sobre processos e pastas. Caminhos vão sempre como argumento separado
/// (<see cref="ProcessStartInfo.ArgumentList"/>), nunca concatenados em linha de shell, e só se forem locais.
/// Encerramentos conferem a identidade (pid + CreateTime) pelo mesmo handle que encerra.
/// </summary>
internal sealed class ProcessActions(IProcessStarter starter, IProcessKiller killer, int ownPid)
{
    /// <param name="startTime">
    /// <see cref="ProcessInfo.StartTime"/> (ISO) que a UI viu; sem ele, vale o CreateTime do último snapshot para o pid.
    /// </param>
    public OperationResult<int> KillProcess(int pid, bool tree, IReadOnlyList<ProcessInfo> snapshot, string? startTime = null)
    {
        var target = snapshot.FirstOrDefault(p => p.Pid == pid);
        long expected;
        if (startTime is not null)
        {
            if (!TryParseStartTime(startTime, out expected))
            {
                return OperationResult<int>.Failure("Horário de início do processo inválido (startTime).");
            }
        }
        else if (target is not null)
        {
            expected = target.CreateTime;
        }
        else
        {
            return OperationResult<int>.Failure($"Processo {pid} não está na lista atual; atualize a lista.");
        }

        var descendants = tree && target is not null && target.CreateTime == expected
            ? ProcessTree.Descendants(target, snapshot).Select(ToTarget).ToList()
            : [];
        return ProcessTerminator.Kill(killer, new KillTarget(pid, expected, target?.Name), descendants, ownPid);
    }

    public OperationResult<int> KillGroup(string? key, IReadOnlyList<AppGroup> groups)
    {
        var group = groups.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.Ordinal));
        if (group is null)
        {
            return OperationResult<int>.Failure("Aplicação não encontrada (talvez já tenha sido encerrada).");
        }

        var result = ProcessTerminator.Kill(killer, root: null, group.Processes.Select(ToTarget).ToList(), ownPid);
        return result.Ok && result.Value == 0
            ? OperationResult<int>.Failure("Aplicação não encontrada (talvez já tenha sido encerrada).")
            : result;
    }

    public OperationResult<bool> OpenFolder(string? path)
    {
        var folder = ValidateFolder(path);
        if (!folder.Ok)
        {
            return OperationResult<bool>.Failure(folder.Error ?? "Pasta inválida.");
        }

        var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(folder.Value!);
        return Start(psi, "Não foi possível abrir o Explorer.");
    }

    public OperationResult<bool> OpenTerminal(string? path)
    {
        var folder = ValidateFolder(path);
        if (!folder.Ok)
        {
            return OperationResult<bool>.Failure(folder.Error ?? "Pasta inválida.");
        }

        var windowsTerminal = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
        windowsTerminal.ArgumentList.Add("-d");
        windowsTerminal.ArgumentList.Add(folder.Value!);

        try
        {
            starter.Start(windowsTerminal);
            return OperationResult<bool>.Success(true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Windows Terminal não instalado: usa o cmd na pasta
        }

        var cmd = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = true,
            WorkingDirectory = folder.Value!,
        };
        return Start(cmd, "Não foi possível abrir o terminal.");
    }

    /// <summary>ISO 8601 (round-trip) → FILETIME UTC, com a mesma precisão de <see cref="ProcessInfo.StartTime"/>.</summary>
    internal static bool TryParseStartTime(string text, out long fileTime)
    {
        fileTime = 0;
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return false;
        }

        try
        {
            fileTime = parsed.ToFileTimeUtc();
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static KillTarget ToTarget(ProcessInfo process) => new(process.Pid, process.CreateTime, process.Name);

    private static OperationResult<string> ValidateFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return OperationResult<string>.Failure("Informe a pasta.");
        }

        if (!LocalPath.TryGetLocal(path, out var full))
        {
            return OperationResult<string>.Failure("Só é possível abrir pastas locais (caminho absoluto numa unidade fixa ou removível).");
        }

        return Directory.Exists(full)
            ? OperationResult<string>.Success(full)
            : OperationResult<string>.Failure($"A pasta \"{path}\" não existe.");
    }

    private OperationResult<bool> Start(ProcessStartInfo psi, string error)
    {
        try
        {
            starter.Start(psi);
            return OperationResult<bool>.Success(true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning($"Monitoramento: {error} {ex.GetType().Name}");
            return OperationResult<bool>.Failure(error);
        }
    }
}
