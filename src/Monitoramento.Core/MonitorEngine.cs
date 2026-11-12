using Monitoramento.Core.Actions;
using Monitoramento.Core.Collection;
using Monitoramento.Core.Config;
using Monitoramento.Core.Favorites;
using Monitoramento.Core.Git;
using Monitoramento.Core.Grouping;
using Monitoramento.Core.Icons;
using Monitoramento.Core.Launcher;
using Monitoramento.Core.Metrics;
using Monitoramento.Core.Models;
using Monitoramento.Core.Terminal;

namespace Monitoramento.Core;

/// <summary>
/// Motor de coleta: cada <see cref="Sample"/> é um ciclo completo (processos, CPU, memória, git, agrupamento,
/// processos salvos). Seguro para chamar de threads diferentes (ciclos são serializados).
/// </summary>
public sealed class MonitorEngine : IDisposable
{
    private const int GroupHistoryPoints = 60;
    private static readonly TimeSpan MachineHistoryWindow = TimeSpan.FromMinutes(10);

    private readonly EngineDependencies _deps;
    private readonly Lock _sampleGate = new();
    private readonly CpuUsageTracker _cpu = new();
    private readonly MachineCpuTracker _machineCpu = new();
    private readonly MachineHistory _machineHistory = new(MachineHistoryWindow);
    private readonly GroupMemoryHistory _groupHistory = new(GroupHistoryPoints);
    private volatile Snapshot? _lastSnapshot;
    private volatile IReadOnlyList<ProcessInfo> _lastProcesses = [];
    private bool _disposed;

    /// <param name="configPath">Caminho do config.json (o host usa %APPDATA%\Monitoramento\config.json).</param>
    public MonitorEngine(string configPath)
        : this(configPath, EngineDependencies.CreateDefault())
    {
    }

    internal MonitorEngine(string configPath, EngineDependencies dependencies)
    {
        _deps = dependencies;
        ConfigService = new ConfigService(new ConfigStore(configPath));
        Terminals = new TerminalManager(dependencies.Pty, dependencies.Shell, dependencies.Time);
        Launcher = new SavedProcessLauncher(dependencies.Starter, dependencies.Killer, dependencies.Time, dependencies.OwnPid, Terminals);
        Actions = new ProcessActions(dependencies.Starter, dependencies.Killer, dependencies.OwnPid);
        KnownRepos = dependencies.GitDetails?.Known ?? new KnownGitRepos(dependencies.Time);
        GitProjects = new GitProjectService(ConfigService, KnownRepos, dependencies.Git, () => _lastProcesses, dependencies.Time);
        GitProjects.RefreshPins();
        if (dependencies.GitDetails is { } gitDetails)
        {
            // dirtyCount dos worktrees conhecidos vem do mesmo cache de status do snapshot (nenhum git a mais)
            gitDetails.StatusDirtyCount = root => dependencies.Git.Resolve(root, null)?.DirtyCount;
        }
    }

    /// <summary>Caminho padrão da configuração do usuário.</summary>
    public static string DefaultConfigPath => ConfigStore.DefaultPath;

    /// <summary>Configuração corrente (favoritos, processos salvos e preferências).</summary>
    public AppConfig Config => ConfigService.Current;

    /// <summary>Motivo pelo qual a configuração não pôde ser lida (arquivo corrompido etc.), ou null.</summary>
    public string? ConfigLoadError => ConfigService.LoadError;

    /// <summary>Último snapshot coletado, ou null antes do primeiro <see cref="Sample"/>.</summary>
    public Snapshot? LastSnapshot => _lastSnapshot;

    public IconProvider Icons => _deps.Icons;

    internal ConfigService ConfigService { get; }

    internal SavedProcessLauncher Launcher { get; }

    internal ProcessActions Actions { get; }

    internal IReadOnlyList<ProcessInfo> LastProcesses => _lastProcesses;

    internal int OwnPid => _deps.OwnPid;

    /// <summary>Terminais embutidos (ConPTY).</summary>
    internal TerminalManager Terminals { get; }

    /// <summary>Quantos terminais embutidos estão rodando (o host confirma antes de sair).</summary>
    public int RunningTerminals => Terminals.RunningCount;

    /// <summary>Encerra todos os terminais embutidos (árvores e pseudo-consoles). Também feito no Dispose.</summary>
    public void CloseTerminals() => Terminals.CloseAll();

    /// <summary>Repositórios que a visão Git e as ações aceitam (vistos nos snapshots ou fixados por projetos/abas).</summary>
    internal KnownGitRepos KnownRepos { get; }

    /// <summary>Projetos git salvos, workspace git e scripts de projeto.</summary>
    internal GitProjectService GitProjects { get; }

    /// <summary>Visão Git; null sem git instalado.</summary>
    internal GitDetailsService? GitDetails => _deps.GitDetails;

    /// <summary>Ações git; null sem git instalado.</summary>
    internal GitActionService? GitActions => _deps.GitActions;

    /// <summary>Executa um ciclo de coleta e devolve o snapshot.</summary>
    public Snapshot Sample()
    {
        lock (_sampleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var raw = _deps.Processes.ReadAll();
            if (raw is null)
            {
                // falha transitória da coleta: reaproveita o ciclo anterior (não derruba o rastreio dos salvos)
                if (_lastSnapshot is { } previous)
                {
                    return previous;
                }

                raw = [];
            }

            var cores = Math.Max(1, _deps.System.LogicalCores);
            var timestamp = _deps.Time.GetElapsedTime(0).Ticks;
            var cpu = _cpu.Update(raw, timestamp, cores);
            var memory = _deps.System.ReadMemory();
            var machineCpu = _machineCpu.Update(_deps.System.ReadCpuTimes(), timestamp);
            _deps.Details.Prune(raw);

            var config = ConfigService.Current;
            var favorites = config.Favorites.ToHashSet(StringComparer.Ordinal);
            var processes = BuildProcesses(raw, cpu, memory.TotalBytes, favorites);

            var (computed, owners) = Launcher.ComputeStates(config.Saved, processes, _deps.Git);
            var states = computed.Select(s => s with { TerminalId = Terminals.TerminalIdForSaved(s.Id) }).ToList();
            if (owners.Count > 0)
            {
                for (var i = 0; i < processes.Count; i++)
                {
                    if (owners.TryGetValue(processes[i].Pid, out var savedId))
                    {
                        processes[i] = processes[i] with { SavedId = savedId };
                    }
                }
            }

            var groups = AppGrouper.Build(processes, memory.TotalBytes, favorites, _deps.Names, _groupHistory);
            _groupHistory.RetainOnly(groups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal));

            var now = _deps.Time.GetUtcNow();
            var used = Math.Max(0, memory.TotalBytes - memory.AvailableBytes);
            var memoryPercent = Round(AppGrouper.Percent(used, memory.TotalBytes));
            _machineHistory.Add(new MachineHistoryPoint(now.ToUnixTimeMilliseconds(), memoryPercent, Round(machineCpu)));

            var snapshot = new Snapshot(
                now.ToString("o"),
                new MachineInfo(memory.TotalBytes, used, memoryPercent, Round(machineCpu), cores, processes.Count, _machineHistory.Points),
                groups,
                states,
                Terminals.List(GitProjects.ProjectFolderOf));

            RegisterSeenRepos(processes, states);
            _lastProcesses = processes;
            _lastSnapshot = snapshot;
            return snapshot;
        }
    }

    public void Dispose()
    {
        lock (_sampleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Terminals.Dispose();
            foreach (var owned in _deps.Owned)
            {
                owned.Dispose();
            }
        }
    }

    private List<ProcessInfo> BuildProcesses(IReadOnlyList<RawProcess> raw, double[] cpu, long totalMemory, HashSet<string> favorites)
    {
        var processes = new List<ProcessInfo>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var p = raw[i];
            if (p.Pid == 0)
            {
                continue; // "System Idle Process": não é um processo real
            }

            var details = _deps.Details.Get(p);
            var git = _deps.Git.Resolve(details.WorkingDirectory, details.CommandLine);
            var favoriteKey = FavoriteKeys.ForProcess(p.Name, details.CommandLine);
            processes.Add(new ProcessInfo
            {
                Pid = p.Pid,
                ParentPid = p.ParentPid,
                Name = p.Name,
                ExePath = details.ExePath,
                CommandLine = details.CommandLine,
                WorkingDirectory = details.WorkingDirectory,
                MemoryBytes = p.PrivateBytes,
                MemoryPercent = Round(AppGrouper.Percent(p.PrivateBytes, totalMemory)),
                CpuPercent = Round(cpu[i]),
                StartTime = p.CreateTime > 0 ? DateTime.FromFileTimeUtc(p.CreateTime).ToString("o") : null,
                AccessDenied = details.AccessDenied,
                IsGitProcess = _deps.GitClassifier.IsGitProcess(p.Name, details.ExePath),
                Git = git,
                SubRepos = git is null ? _deps.Git.ResolveChildren(details.WorkingDirectory) : [],
                FavoriteKey = favoriteKey,
                Favorite = favorites.Contains(favoriteKey),
                CreateTime = p.CreateTime,
            });
        }

        return processes;
    }

    /// <summary>Repositórios vistos neste ciclo: os únicos que a visão Git e as ações aceitam.</summary>
    private void RegisterSeenRepos(List<ProcessInfo> processes, IReadOnlyList<SavedProcessState> states)
    {
        var known = KnownRepos;
        var roots = processes
            .SelectMany(p => p.SubRepos.Select(r => r.RepoRoot).Append(p.Git?.RepoRoot))
            .Concat(states.Select(s => s.Git?.RepoRoot));
        foreach (var root in roots.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            known.Register(root);
        }
    }

    private static double Round(double value) => Math.Round(value, 2);
}
