using System.Diagnostics;
using Monitoramento.Core.Actions;
using Monitoramento.Core.Collection;
using Monitoramento.Core.Git;
using Monitoramento.Core.Grouping;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Tests.TestSupport;

internal sealed class FakeNames : IFriendlyNameProvider
{
    public string Resolve(string exeName, string? exePath) => "Nome " + System.IO.Path.GetFileNameWithoutExtension(exeName);
}

internal sealed class FakeProcessSource : IProcessSource
{
    public List<RawProcess> Processes { get; } = [];

    /// <summary>Simula falha transitória do NtQuerySystemInformation.</summary>
    public bool Fail { get; set; }

    public IReadOnlyList<RawProcess>? ReadAll() => Fail ? null : Processes.ToList();
}

internal sealed class FakeDetailsProvider : IProcessDetailsProvider
{
    public Dictionary<int, ProcessDetails> Details { get; } = [];

    public ProcessDetails Get(in RawProcess process) =>
        Details.TryGetValue(process.Pid, out var details) ? details : new ProcessDetails(null, null, null, AccessDenied: true);

    public void Prune(IReadOnlyList<RawProcess> alive)
    {
    }
}

internal sealed class FakeSystemInfo : ISystemInfoSource
{
    public MemoryInfo Memory { get; set; } = new(TotalBytes: 16_000, AvailableBytes: 4_000);

    public SystemCpuTimes CpuTimes { get; set; } = new(Idle: 0, Kernel: 0, User: 0);

    public int LogicalCores { get; set; } = 4;

    public MemoryInfo ReadMemory() => Memory;

    public SystemCpuTimes ReadCpuTimes() => CpuTimes;
}

internal sealed class FakeGitResolver : IGitInfoResolver
{
    public Dictionary<string, GitInfo> ByDirectory { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Chamado antes de resolver; permite bloquear para testar que o I/O fica fora dos locks.</summary>
    public Action<string?>? OnResolve { get; set; }

    public GitInfo? Resolve(string? workingDirectory, string? commandLine)
    {
        OnResolve?.Invoke(workingDirectory);
        return workingDirectory is not null && ByDirectory.TryGetValue(workingDirectory, out var info) ? info : null;
    }

    public Dictionary<string, IReadOnlyList<GitInfo>> ChildrenByDirectory { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<GitInfo> ResolveChildren(string? workingDirectory) =>
        workingDirectory is not null && ChildrenByDirectory.TryGetValue(workingDirectory, out var children) ? children : [];
}

internal sealed class FakeStarter : IProcessStarter
{
    private readonly Lock _gate = new();
    private readonly List<ProcessStartInfo> _started = [];
    private readonly List<LaunchSpec> _launched = [];

    /// <summary>Ações de pasta (Explorer/terminal).</summary>
    public IReadOnlyList<ProcessStartInfo> Started
    {
        get
        {
            lock (_gate)
            {
                return _started.ToList();
            }
        }
    }

    /// <summary>Processos salvos iniciados.</summary>
    public IReadOnlyList<LaunchSpec> Launched
    {
        get
        {
            lock (_gate)
            {
                return _launched.ToList();
            }
        }
    }

    public Func<ProcessStartInfo, StartedProcess>? OnStart { get; set; }

    public Func<LaunchSpec, StartedProcess>? OnLaunch { get; set; }

    public TimeSpan Delay { get; set; }

    public StartedProcess Start(ProcessStartInfo startInfo)
    {
        lock (_gate)
        {
            _started.Add(startInfo);
        }

        return OnStart?.Invoke(startInfo) ?? new StartedProcess(5000, 123);
    }

    public StartedProcess Launch(LaunchSpec spec)
    {
        lock (_gate)
        {
            _launched.Add(spec);
        }

        if (Delay > TimeSpan.Zero)
        {
            Thread.Sleep(Delay);
        }

        return OnLaunch?.Invoke(spec) ?? new StartedProcess(5000, 123);
    }
}

/// <summary>Processos "vivos" registrados com (CreateTime, nome); encerra pelo handle e registra os pids.</summary>
internal sealed class FakeKiller : IProcessKiller
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, (long CreateTime, string Name)> _alive = [];
    private readonly List<int> _killed = [];

    public IReadOnlyList<int> Killed
    {
        get
        {
            lock (_gate)
            {
                return _killed.ToList();
            }
        }
    }

    public Exception? ThrowOnTerminate { get; set; }

    public Exception? ThrowOnOpen { get; set; }

    public void Register(int pid, long createTime, string name)
    {
        lock (_gate)
        {
            _alive[pid] = (createTime, name);
        }
    }

    public void Register(IEnumerable<ProcessInfo> processes)
    {
        foreach (var p in processes)
        {
            Register(p.Pid, p.CreateTime, p.Name);
        }
    }

    /// <summary>O pid passou a ser outro processo (ou o mesmo, renomeado) depois do snapshot.</summary>
    public void Replace(int pid, long createTime, string name) => Register(pid, createTime, name);

    public void Remove(int pid)
    {
        lock (_gate)
        {
            _alive.Remove(pid);
        }
    }

    public IProcessHandle? Open(int pid)
    {
        if (ThrowOnOpen is not null)
        {
            throw ThrowOnOpen;
        }

        lock (_gate)
        {
            return _alive.TryGetValue(pid, out var p) ? new Handle(this, pid, p.CreateTime, p.Name) : null;
        }
    }

    private void Terminate(int pid)
    {
        if (ThrowOnTerminate is not null)
        {
            throw ThrowOnTerminate;
        }

        lock (_gate)
        {
            _killed.Add(pid);
            _alive.Remove(pid);
        }
    }

    private sealed class Handle(FakeKiller owner, int pid, long createTime, string name) : IProcessHandle
    {
        public long CreateTime => createTime;

        public string? Name => name;

        public void Terminate() => owner.Terminate(pid);

        public void Dispose()
        {
        }
    }
}

internal static class Proc
{
    public static ProcessInfo Info(
        int pid,
        string name,
        long memory,
        double cpu = 0,
        string? exePath = null,
        GitInfo? git = null,
        int parentPid = 1,
        long createTime = 1,
        string? commandLine = null,
        string? cwd = null) => new()
        {
            Pid = pid,
            ParentPid = parentPid,
            Name = name,
            ExePath = exePath,
            MemoryBytes = memory,
            CpuPercent = cpu,
            Git = git,
            CreateTime = createTime,
            CommandLine = commandLine,
            WorkingDirectory = cwd,
            FavoriteKey = "cmd:000000000000",
        };
}
