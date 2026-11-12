using Monitoramento.Core.Collection;
using Monitoramento.Core.Git;
using Monitoramento.Core.Icons;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Tests.TestSupport;

/// <summary>MonitorEngine montado com fontes falsas (sem Win32) e config numa pasta temporária.</summary>
internal sealed class EngineHarness : IDisposable
{
    public const int OwnPid = 999;

    private readonly Lazy<MonitorEngine> _engine;

    public EngineHarness()
    {
        ConfigPath = Temp.Combine("config.json");
        _engine = new Lazy<MonitorEngine>(() => new MonitorEngine(ConfigPath, new EngineDependencies
        {
            Processes = Source,
            Details = Details,
            System = System,
            Git = Git,
            Names = new FakeNames(),
            Starter = Starter,
            Killer = Killer,
            GitClassifier = new GitProcessClassifier(@"C:\Program Files\Git"),
            Icons = new IconProvider(),
            OwnPid = OwnPid,
            Time = Time,
            GitDetails = GitDetails,
            GitActions = GitActions,
            Pty = Pty,
            Shell = () => new Monitoramento.Core.Terminal.TerminalLaunch(@"C:\pwsh\pwsh.exe", "pwsh -NoLogo", new Dictionary<string, string>()),
        }));
        var gitExe = GitExecutable.Find();
        if (gitExe is not null)
        {
            var runner = new GitProcessRunner(gitExe);
            GitDetails = new GitDetailsService(runner, new KnownGitRepos(Time), Time);
            GitActions = new GitActionService(GitDetails, runner);
        }
    }

    /// <summary>Visão Git real (git instalado); null se o git não estiver no PATH.</summary>
    public GitDetailsService? GitDetails { get; }

    public GitActionService? GitActions { get; }

    public TempDirectory Temp { get; } = new();

    public string ConfigPath { get; }

    public FakeProcessSource Source { get; } = new();

    public FakeDetailsProvider Details { get; } = new();

    public FakeSystemInfo System { get; } = new();

    public FakeGitResolver Git { get; } = new();

    public FakeStarter Starter { get; } = new();

    public FakeKiller Killer { get; } = new();

    /// <summary>Pseudo-consoles falsos (os testes de ConPTY de verdade ficam em ConPtyIntegrationTests).</summary>
    public FakePtyFactory Pty { get; } = new();

    public ManualTimeProvider Time { get; } = new();

    public MonitorEngine Engine => _engine.Value;

    public void AddProcess(int pid, string name, long memory, string? commandLine = null, string? cwd = null, int parentPid = 1, long cpu = 0, long createTime = 1, string? exePath = null)
    {
        Source.Processes.RemoveAll(p => p.Pid == pid);
        Source.Processes.Add(new RawProcess(pid, parentPid, name, memory, cpu, createTime));
        Details.Details[pid] = new ProcessDetails(exePath, commandLine, cwd, AccessDenied: false);
        Killer.Register(pid, createTime, name);
    }

    public void Dispose()
    {
        if (_engine.IsValueCreated)
        {
            _engine.Value.Dispose();
        }

        Temp.Dispose();
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _now.UtcTicks;

    public void Advance(TimeSpan by) => _now += by;
}
