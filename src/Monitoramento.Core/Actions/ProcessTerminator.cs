using System.ComponentModel;

namespace Monitoramento.Core.Actions;

/// <summary>Processo a encerrar: pid, CreateTime esperado (FILETIME UTC) e o nome visto no snapshot (se houver).</summary>
internal readonly record struct KillTarget(int Pid, long CreateTime, string? SnapshotName);

/// <summary>
/// Encerramento com identidade conferida: abre cada processo, compara o CreateTime com o esperado e checa a
/// proteção com o nome atual (e o do snapshot), e só então encerra pelo mesmo handle. Nunca encerra um pid
/// reaproveitado por outro processo.
/// </summary>
internal static class ProcessTerminator
{
    public const string ReusedMessage = "O processo terminou e o PID foi reutilizado; atualize a lista.";

    /// <returns>Erro em português, ou null se encerrou.</returns>
    public static string? KillOne(IProcessKiller killer, int pid, long expectedCreateTime, string? snapshotName, int ownPid) =>
        Kill(killer, new KillTarget(pid, expectedCreateTime, snapshotName), [], ownPid).Error;

    /// <summary>
    /// <paramref name="root"/> é obrigatório: sumido, reaproveitado ou sem acesso → erro e nada é encerrado.
    /// Os <paramref name="others"/> (descendentes, instâncias, membros de processo salvo) que sumiram ou foram
    /// reaproveitados são pulados. Qualquer processo protegido recusa a operação inteira.
    /// </summary>
    /// <returns>Quantos processos foram encerrados.</returns>
    public static OperationResult<int> Kill(IProcessKiller killer, KillTarget? root, IReadOnlyList<KillTarget> others, int ownPid)
    {
        var opened = new List<(KillTarget Target, IProcessHandle Handle)>();
        try
        {
            string? skipped = null;
            IReadOnlyList<KillTarget> targets = root is { } r ? [r, .. others] : others;
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                var required = root is not null && i == 0;
                var prefix = root is not null && !required ? "A árvore contém um processo protegido: " : string.Empty;

                if (ProcessProtection.CheckKill(target.Pid, target.SnapshotName, ownPid) is { } refusal)
                {
                    return OperationResult<int>.Failure(prefix + refusal);
                }

                IProcessHandle? handle;
                try
                {
                    handle = killer.Open(target.Pid);
                }
                catch (Win32Exception)
                {
                    var denied = AccessDenied(target.Pid);
                    if (required)
                    {
                        return OperationResult<int>.Failure(denied);
                    }

                    skipped = denied;
                    continue;
                }

                if (handle is null)
                {
                    if (required)
                    {
                        return OperationResult<int>.Failure(NotFound(target.Pid));
                    }

                    continue;
                }

                if (handle.CreateTime != target.CreateTime)
                {
                    handle.Dispose();
                    if (required)
                    {
                        return OperationResult<int>.Failure(ReusedMessage);
                    }

                    skipped = ReusedMessage;
                    continue;
                }

                opened.Add((target, handle));
                if (ProcessProtection.CheckKill(target.Pid, handle.Name, ownPid) is { } currentRefusal)
                {
                    return OperationResult<int>.Failure(prefix + currentRefusal);
                }
            }

            var killed = 0;
            string? error = null;
            foreach (var (target, handle) in opened)
            {
                try
                {
                    handle.Terminate();
                    killed++;
                }
                catch (Win32Exception)
                {
                    error = AccessDenied(target.Pid);
                }
            }

            error ??= skipped;
            return killed == 0 && error is not null
                ? OperationResult<int>.Failure(error)
                : OperationResult<int>.Success(killed);
        }
        finally
        {
            foreach (var (_, handle) in opened)
            {
                handle.Dispose();
            }
        }
    }

    public static string NotFound(int pid) => $"Processo {pid} não encontrado (talvez já tenha terminado).";

    private static string AccessDenied(int pid) => $"Não foi possível encerrar o processo {pid}: acesso negado.";
}
