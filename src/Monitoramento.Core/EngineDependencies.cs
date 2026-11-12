using Monitoramento.Core.Actions;
using Monitoramento.Core.Collection;
using Monitoramento.Core.Git;
using Monitoramento.Core.Grouping;
using Monitoramento.Core.Icons;
using Monitoramento.Core.Interop;
using Monitoramento.Core.Terminal;

namespace Monitoramento.Core;

/// <summary>Peças do motor. Produção usa <see cref="CreateDefault"/>; testes trocam por falsos sem Win32.</summary>
internal sealed class EngineDependencies
{
    public static readonly TimeSpan GitStatusTtl = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan GitStatusTimeout = TimeSpan.FromSeconds(3);

    public required IProcessSource Processes { get; init; }

    public required IProcessDetailsProvider Details { get; init; }

    public required ISystemInfoSource System { get; init; }

    public required IGitInfoResolver Git { get; init; }

    public required IFriendlyNameProvider Names { get; init; }

    public required IProcessStarter Starter { get; init; }

    public required IProcessKiller Killer { get; init; }

    public required GitProcessClassifier GitClassifier { get; init; }

    public required IconProvider Icons { get; init; }

    public required int OwnPid { get; init; }

    public required TimeProvider Time { get; init; }

    /// <summary>Visão Git (gitDetails); null quando o git não está instalado.</summary>
    public GitDetailsService? GitDetails { get; init; }

    /// <summary>Ações git do painel (commit, merge, branch…); null quando o git não está instalado.</summary>
    public GitActionService? GitActions { get; init; }

    /// <summary>Pseudo-consoles dos terminais embutidos (ConPTY); testes trocam por um falso.</summary>
    public IPtyFactory Pty { get; init; } = new ConPtyFactory();

    /// <summary>Shell padrão do termOpen.</summary>
    public Func<TerminalLaunch> Shell { get; init; } = ShellLocator.Find;

    /// <summary>Recursos que o motor descarta no Dispose.</summary>
    public IReadOnlyList<IDisposable> Owned { get; init; } = [];

    public static EngineDependencies CreateDefault()
    {
        var time = TimeProvider.System;
        var gitExe = GitExecutable.Find();
        var statusCache = gitExe is null ? null : new GitStatusCache(gitExe, GitStatusTtl, GitStatusTimeout, time);
        var gitRunner = gitExe is null ? null : new GitProcessRunner(gitExe);
        var gitDetails = gitRunner is null ? null : new GitDetailsService(gitRunner, new KnownGitRepos(time), time);

        return new EngineDependencies
        {
            Processes = new NtProcessSource(),
            Details = new CachedProcessDetailsProvider(new NativeProcessReader(), () => (long)time.GetElapsedTime(0).TotalMilliseconds),
            System = new NativeSystemInfoSource(),
            Git = new GitInfoResolver(statusCache, time),
            Names = new FileVersionNameProvider(),
            Starter = new SystemProcessStarter(),
            Killer = new SystemProcessKiller(),
            GitClassifier = new GitProcessClassifier(GitExecutable.InstallDirectoryOf(gitExe)),
            Icons = new IconProvider(),
            OwnPid = Environment.ProcessId,
            Time = time,
            GitDetails = gitDetails,
            GitActions = gitDetails is null || gitRunner is null ? null : new GitActionService(gitDetails, gitRunner),
            Owned = statusCache is null ? [] : [statusCache],
        };
    }
}
