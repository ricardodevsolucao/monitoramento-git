using System.ComponentModel;
using System.Diagnostics;
using Monitoramento.Core.Actions;
using Monitoramento.Core.Git;
using Monitoramento.Core.Models;
using Monitoramento.Core.Security;
using Monitoramento.Core.Terminal;

namespace Monitoramento.Core.Launcher;

/// <summary>Terminais embutidos, do ponto de vista do launcher (modo "terminal").</summary>
internal interface ISavedTerminalHost
{
    bool IsSupported { get; }

    OperationResult<SavedTerminalStart> StartSaved(SavedProcess saved, TerminalLaunch launch, string folder);
}

/// <summary>
/// Inicia/para processos salvos e calcula o estado de cada um a partir do snapshot.
/// O início é atômico por id (no máximo um em andamento/rodando); I/O (git, criação de processo) fica fora do lock.
/// Modo "terminal" roda o mesmo <c>cmd /s /k</c> num terminal embutido; sem ConPTY, cai para "window".
/// </summary>
internal sealed class SavedProcessLauncher(IProcessStarter starter, IProcessKiller killer, TimeProvider time, int ownPid, ISavedTerminalHost? terminals = null)
{
    /// <summary>Tempo que um início recente continua "rodando" mesmo antes de aparecer na coleta.</summary>
    private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(5);

    private const string AlreadyRunning = "Este processo salvo já está em execução.";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, LaunchRecord> _launches = new(StringComparer.Ordinal);
    private readonly HashSet<string> _starting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);

    /// <param name="alreadyRunning">Já há processo correspondente no último snapshot (iniciado fora do app, por exemplo).</param>
    public OperationResult<SavedProcessState> Start(SavedProcess saved, bool alreadyRunning)
    {
        if (alreadyRunning)
        {
            return OperationResult<SavedProcessState>.Failure(AlreadyRunning);
        }

        if (string.IsNullOrWhiteSpace(saved.Command))
        {
            return Fail(saved, "O processo salvo não tem comando.");
        }

        var launchable = saved;
        if (!string.IsNullOrWhiteSpace(saved.WorkingDirectory))
        {
            if (!LocalPath.TryGetLocal(saved.WorkingDirectory, out var folder))
            {
                return Fail(saved, "A pasta de trabalho precisa ser uma pasta local (unidade fixa ou removível).");
            }

            if (!Directory.Exists(folder))
            {
                return Fail(saved, $"A pasta de trabalho \"{saved.WorkingDirectory}\" não existe.");
            }

            launchable = saved with { WorkingDirectory = folder };
        }

        // check-then-start atômico: o registro de início (vivo ou em carência) ou um início em andamento bloqueia outro
        lock (_gate)
        {
            if (_launches.ContainsKey(saved.Id) || !_starting.Add(saved.Id))
            {
                return OperationResult<SavedProcessState>.Failure(AlreadyRunning);
            }
        }

        StartedProcess? started = null;
        string? error = null;
        string? terminalId = null;
        var startedAt = time.GetUtcNow();
        try
        {
            var spec = LaunchCommandBuilder.Build(launchable, Environment.SystemDirectory);
            if (LaunchModes.Normalize(saved.LaunchMode) == LaunchModes.Terminal && terminals is { IsSupported: true } host)
            {
                var folder = launchable.WorkingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var result = host.StartSaved(launchable, new TerminalLaunch(spec.ApplicationName, spec.CommandLine, spec.Environment), folder);
                if (result.Ok)
                {
                    started = result.Value!.Process;
                    terminalId = result.Value.Terminal.Id;
                }
                else
                {
                    error = result.Error;
                }
            }
            else
            {
                started = starter.Launch(spec);
            }
        }
        catch (Win32Exception ex)
        {
            error = $"Não foi possível iniciar o processo (erro {ex.NativeErrorCode}).";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Trace.TraceWarning($"Monitoramento: falha ao iniciar processo salvo: {ex.GetType().Name}");
            error = "Não foi possível iniciar o processo.";
        }
        finally
        {
            lock (_gate)
            {
                _starting.Remove(saved.Id);
                if (started is not null)
                {
                    _launches[saved.Id] = new LaunchRecord(started.Pid, started.CreateTime, startedAt);
                    _errors.Remove(saved.Id);
                }
            }
        }

        if (started is null)
        {
            return Fail(saved, error ?? "Não foi possível iniciar o processo.");
        }

        return OperationResult<SavedProcessState>.Success(new SavedProcessState(saved)
        {
            Running = true,
            Pids = started.Pid > 0 ? [started.Pid] : [],
            StartedAt = startedAt.ToString("o"),
            TerminalId = terminalId,
        });
    }

    /// <summary>
    /// Encerra os processos do salvo presentes em <paramref name="processes"/> (identidade = pid + CreateTime) e o
    /// processo raiz iniciado pelo app, conferido pelo CreateTime do registro. Pids reaproveitados nunca são encerrados.
    /// </summary>
    public OperationResult<SavedProcessState> Stop(SavedProcess saved, IReadOnlyList<SavedProcess> savedList, IReadOnlyList<ProcessInfo> processes)
    {
        LaunchRecord? launch;
        IReadOnlyList<ProcessInfo> owned;
        lock (_gate)
        {
            launch = _launches.GetValueOrDefault(saved.Id);
            owned = Owned(saved, savedList, processes);
        }

        var targets = new List<KillTarget>();
        if (launch is { Pid: > 0 })
        {
            targets.Add(new KillTarget(launch.Pid, launch.CreateTime, null));
        }

        foreach (var process in owned.OrderBy(p => p.CreateTime))
        {
            if (launch is null || process.Pid != launch.Pid)
            {
                targets.Add(new KillTarget(process.Pid, process.CreateTime, process.Name));
            }
        }

        var result = ProcessTerminator.Kill(killer, root: null, targets, ownPid);

        // pid reaproveitado = o processo do salvo já terminou; não é falha do Parar
        var error = result.Ok || result.Error == ProcessTerminator.ReusedMessage ? null : result.Error;

        lock (_gate)
        {
            if (launch is not null && _launches.TryGetValue(saved.Id, out var current) && current == launch)
            {
                _launches.Remove(saved.Id);
            }

            if (error is null)
            {
                _errors.Remove(saved.Id);
            }
            else
            {
                _errors[saved.Id] = error;
            }
        }

        var state = new SavedProcessState(saved) { Running = false, LastError = error };
        return error is null ? OperationResult<SavedProcessState>.Success(state) : OperationResult<SavedProcessState>.Failure(error);
    }

    public void Forget(string id)
    {
        lock (_gate)
        {
            _launches.Remove(id);
            _errors.Remove(id);
        }
    }

    /// <summary>Estado de cada processo salvo e o mapa pid → id do salvo dono do processo.</summary>
    public (IReadOnlyList<SavedProcessState> States, IReadOnlyDictionary<int, string> OwnerByPid) ComputeStates(
        IReadOnlyList<SavedProcess> savedList,
        IReadOnlyList<ProcessInfo> processes,
        IGitInfoResolver git)
    {
        // I/O do git fora do lock (Q12)
        var gitInfos = savedList
            .Select(s => string.IsNullOrWhiteSpace(s.WorkingDirectory) ? null : git.Resolve(s.WorkingDirectory, null))
            .ToArray();

        var owners = new Dictionary<int, string>();
        var states = new List<SavedProcessState>(savedList.Count);
        var now = time.GetUtcNow();

        lock (_gate)
        {
            var matches = new SavedProcessMatcher(processes).MatchAll(savedList, id => _launches.GetValueOrDefault(id));
            for (var i = 0; i < savedList.Count; i++)
            {
                var saved = savedList[i];
                var owned = matches[i];
                _launches.TryGetValue(saved.Id, out var launch);

                // ciclo sem processos (coleta falhou): não descarta o rastreio
                if (launch is not null && owned.Count == 0 && processes.Count > 0 && now - launch.StartedAt > StartGrace)
                {
                    _launches.Remove(saved.Id); // terminou sozinho
                    launch = null;
                }

                foreach (var process in owned)
                {
                    owners.TryAdd(process.Pid, saved.Id);
                }

                var oldest = owned.MinBy(p => p.CreateTime);
                states.Add(new SavedProcessState(saved)
                {
                    Running = owned.Count > 0 || launch is not null || _starting.Contains(saved.Id),
                    Pids = owned.Count > 0 ? [.. owned.Select(p => p.Pid).Order()] : launch is { Pid: > 0 } ? [launch.Pid] : [],
                    MemoryBytes = owned.Sum(p => p.MemoryBytes),
                    CpuPercent = Math.Min(100, owned.Sum(p => p.CpuPercent)),
                    StartedAt = launch?.StartedAt.ToString("o") ?? oldest?.StartTime,
                    Git = gitInfos[i],
                    LastError = _errors.GetValueOrDefault(saved.Id),
                });
            }
        }

        return (states, owners);
    }

    /// <summary>Processos que pertencem a <paramref name="saved"/>, com a mesma regra de dono do <see cref="ComputeStates"/>.</summary>
    private IReadOnlyList<ProcessInfo> Owned(SavedProcess saved, IReadOnlyList<SavedProcess> savedList, IReadOnlyList<ProcessInfo> processes)
    {
        var matcher = new SavedProcessMatcher(processes);
        var index = -1;
        for (var i = 0; i < savedList.Count; i++)
        {
            if (string.Equals(savedList[i].Id, saved.Id, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        return index < 0
            ? matcher.Match(saved, _launches.GetValueOrDefault(saved.Id))
            : matcher.MatchAll(savedList, id => _launches.GetValueOrDefault(id))[index];
    }

    private OperationResult<SavedProcessState> Fail(SavedProcess saved, string error)
    {
        lock (_gate)
        {
            _errors[saved.Id] = error;
        }

        return OperationResult<SavedProcessState>.Failure(error);
    }
}
