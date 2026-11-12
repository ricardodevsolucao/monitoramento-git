using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Monitoramento.Core.Git;

/// <summary>
/// <see cref="GitRunMode.Read"/>: leitura automática, blindada (nada da config local que execute programa).
/// <see cref="GitRunMode.Action"/>: ação pedida pelo usuário, roda como o git do terminal (hooks ativos).
/// </summary>
internal enum GitRunMode
{
    Read,
    Action,

    /// <summary>
    /// Git isolado (ex.: <c>git config --file</c>): sem <c>-C</c>, com <see cref="GitRunRequest.RepoRoot"/> só como pasta
    /// de trabalho neutra, sem config global/sistema e sem procurar repositório acima dela.
    /// </summary>
    Isolated,
}

/// <param name="Args">Subcomando e argumentos (vão um a um em ArgumentList; nada é concatenado).</param>
/// <param name="Timeout">Null = <see cref="GitProcessRunner.DefaultReadTimeout"/> (leitura) ou <see cref="GitProcessRunner.DefaultActionTimeout"/>.</param>
/// <param name="MaxOutputBytes">Limite da saída padrão; na leitura, passou → o git é encerrado e o resultado marca Overflow.</param>
/// <param name="StandardInput">Texto enviado pela entrada padrão (UTF-8), ex.: mensagem de commit com <c>-F -</c>.</param>
internal sealed record GitRunRequest(
    string RepoRoot,
    IReadOnlyList<string> Args,
    GitRunMode Mode,
    TimeSpan? Timeout = null,
    int MaxOutputBytes = GitProcessRunner.DefaultMaxOutputBytes,
    string? StandardInput = null);

/// <param name="Output">Saída padrão (UTF-8), até o limite.</param>
/// <param name="Error">Saída de erro (UTF-8), até 64 KB.</param>
internal sealed record GitRunResult(int ExitCode, string Output, string Error, bool TimedOut, bool Overflow, bool StartFailed)
{
    public bool Ok => ExitCode == 0 && !TimedOut && !Overflow && !StartFailed;

    public static GitRunResult FailedToStart() => new(-1, string.Empty, string.Empty, TimedOut: false, Overflow: false, StartFailed: true);
}

internal interface IGitRunner
{
    Task<GitRunResult> RunAsync(GitRunRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Executa o git.exe com ArgumentList, sem shell, sem janela, com timeout (encerra a árvore) e saída limitada.</summary>
internal sealed class GitProcessRunner(string gitExe) : IGitRunner
{
    public const int DefaultMaxOutputBytes = 2 * 1024 * 1024;
    public static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultActionTimeout = TimeSpan.FromSeconds(60);

    private const int MaxErrorBytes = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Opções globais da leitura: desligam tudo que a config local poderia usar para executar um programa
    /// (fsmonitor, hooks, pager, verificação de assinatura, comando de refs alternativas).
    /// </summary>
    private static readonly string[] ReadPrefix =
    [
        "-c", "core.fsmonitor=false",
        "-c", "core.hooksPath=NUL",
        "-c", "core.untrackedCache=false",
        "-c", "core.pager=",
        "-c", "log.showSignature=false",
        "-c", "gpg.program=",
        "-c", "core.alternateRefsCommand=",
        "-c", "i18n.logOutputEncoding=UTF-8",
        "-c", "color.ui=false",
        "--no-optional-locks",
        "--no-pager",
    ];

    /// <summary>Ação do usuário: como no terminal (hooks/assinatura ativos), só sem pager e sem cor.</summary>
    private static readonly string[] ActionPrefix =
    [
        "-c", "core.pager=",
        "-c", "color.ui=false",
        "--no-pager",
    ];

    /// <summary>Variáveis herdadas que redirecionariam o git para outro repositório/índice ou injetariam config/comando.</summary>
    internal static readonly string[] InheritedGitVariables =
    [
        "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES", "GIT_CONFIG_PARAMETERS",
        "GIT_CONFIG_COUNT", "GIT_EXTERNAL_DIFF", "GIT_PAGER", "PAGER",
    ];

    internal static ProcessStartInfo BuildStartInfo(string gitExe, GitRunRequest request)
    {
        var psi = new ProcessStartInfo(gitExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardInputEncoding = Utf8,
        };

        RemoveInheritedGitVariables(psi);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (request.Mode == GitRunMode.Isolated)
        {
            psi.WorkingDirectory = request.RepoRoot;
            psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            psi.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
            psi.Environment["GIT_CEILING_DIRECTORIES"] = Path.GetDirectoryName(request.RepoRoot) ?? request.RepoRoot;
            foreach (var arg in request.Args)
            {
                psi.ArgumentList.Add(arg);
            }

            return psi;
        }

        foreach (var arg in request.Mode == GitRunMode.Read ? ReadPrefix : ActionPrefix)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(request.RepoRoot);
        foreach (var arg in request.Args)
        {
            psi.ArgumentList.Add(arg);
        }

        if (request.Mode == GitRunMode.Read)
        {
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        }
        else
        {
            // nenhum editor interativo (merge/pull sem mensagem): ":" é o "não editar" do próprio git
            psi.Environment["GIT_EDITOR"] = ":";
            psi.Environment["GIT_SEQUENCE_EDITOR"] = ":";
            psi.Environment["GIT_MERGE_AUTOEDIT"] = "no";
        }

        return psi;
    }

    /// <summary>Tira do ambiente do processo filho as variáveis GIT_* herdadas (também usado pelo status do snapshot).</summary>
    internal static void RemoveInheritedGitVariables(ProcessStartInfo psi)
    {
        foreach (var name in InheritedGitVariables)
        {
            psi.Environment.Remove(name);
        }
    }

    public async Task<GitRunResult> RunAsync(GitRunRequest request, CancellationToken cancellationToken = default)
    {
        var timeoutSpan = request.Timeout ?? (request.Mode == GitRunMode.Action ? DefaultActionTimeout : DefaultReadTimeout);
        Process? process = null;
        try
        {
            process = Process.Start(BuildStartInfo(gitExe, request));
            if (process is null)
            {
                return GitRunResult.FailedToStart();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutSpan);

            var killOnOverflow = request.Mode != GitRunMode.Action;
            var stdout = ReadCappedAsync(process.StandardOutput.BaseStream, request.MaxOutputBytes, killOnOverflow ? process : null, timeout.Token);
            var stderr = ReadCappedAsync(process.StandardError.BaseStream, MaxErrorBytes, null, timeout.Token);
            await WriteInputAsync(process, request.StandardInput, timeout.Token).ConfigureAwait(false);

            var (output, overflow) = await stdout.ConfigureAwait(false);
            var (error, _) = await stderr.ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            // ação: a saída extra só é descartada (o git não é interrompido no meio de um commit/push)
            return new GitRunResult(process.ExitCode, Decode(output), Decode(error), TimedOut: false, overflow && killOnOverflow, StartFailed: false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new GitRunResult(-1, string.Empty, string.Empty, TimedOut: true, Overflow: false, StartFailed: false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            TryKill(process);
            return process is null || !HasStarted(process) ? GitRunResult.FailedToStart() : new GitRunResult(-1, string.Empty, string.Empty, false, false, false);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static async Task WriteInputAsync(Process process, string? input, CancellationToken token)
    {
        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // o git terminou antes de ler a entrada (ex.: erro de argumento): o código de saída explica
        }
        finally
        {
            try
            {
                process.StandardInput.Close(); // EOF: nada fica esperando entrada
            }
            catch (IOException)
            {
                // pipe já fechado
            }
        }
    }

    /// <summary>Lê até <paramref name="max"/> bytes; o excedente é descartado (ou o processo é encerrado, na leitura).</summary>
    private static async Task<(byte[] Data, bool Overflow)> ReadCappedAsync(Stream stream, int max, Process? killOnOverflow, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var overflow = false;
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            var room = max - (int)buffer.Length;
            if (read > room)
            {
                buffer.Write(chunk, 0, Math.Max(0, room));
                overflow = true;
                if (killOnOverflow is not null)
                {
                    TryKill(killOnOverflow);
                    break;
                }

                continue;
            }

            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), overflow);
    }

    private static string Decode(byte[] bytes) => Utf8.GetString(bytes);

    private static bool HasStarted(Process process)
    {
        try
        {
            _ = process.Id;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            process?.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // já terminou
        }
    }
}
