using System.Collections.Concurrent;
using Monitoramento.Core.Git;

namespace Monitoramento.Core.Tests.TestSupport;

/// <summary>Repassa ao runner real, conta as execuções e pode atrasar cada uma (para testes de concorrência).</summary>
internal sealed class CountingGitRunner(IGitRunner inner, TimeSpan? delay = null) : IGitRunner
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Subcomando de cada execução (primeiro argumento depois das opções globais).</summary>
    public ConcurrentQueue<string> Commands { get; } = new();

    /// <summary>Argumentos completos de cada execução.</summary>
    public ConcurrentQueue<IReadOnlyList<string>> Requests { get; } = new();

    public static CountingGitRunner Real(TimeSpan? delay = null)
    {
        var gitExe = GitExecutable.Find();
        Assert.NotNull(gitExe);
        return new CountingGitRunner(new GitProcessRunner(gitExe), delay);
    }

    public async Task<GitRunResult> RunAsync(GitRunRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        Commands.Enqueue(request.Args.Count > 0 ? request.Args[0] : string.Empty);
        Requests.Enqueue(request.Args);
        if (delay is { } wait)
        {
            await Task.Delay(wait, cancellationToken);
        }

        return await inner.RunAsync(request, cancellationToken);
    }
}
