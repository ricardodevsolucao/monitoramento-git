namespace Monitoramento.Core.Terminal;

/// <summary>O que iniciar num pseudo-console: executável, linha de comando completa, pasta, variáveis e tamanho.</summary>
/// <param name="Environment">Variáveis acrescentadas ao ambiente do app (elas vencem).</param>
internal sealed record PtyStartInfo(
    string ApplicationName,
    string CommandLine,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    int Cols,
    int Rows);

/// <summary>Sessão de pseudo-console com a árvore de processos dela.</summary>
internal interface IPtySession : IDisposable
{
    int Pid { get; }

    /// <summary>CreateTime (FILETIME UTC) do processo raiz, para o rastreio de identidade dos salvos.</summary>
    long CreateTime { get; }

    /// <summary>Bytes UTF-8 para a entrada do pseudo-console.</summary>
    void Write(ReadOnlySpan<byte> data);

    void Resize(int cols, int rows);

    /// <summary>Ainda há processos vivos na árvore (Job Object), mesmo com o raiz encerrado.</summary>
    bool HasLiveProcesses { get; }

    /// <summary>Encerra a árvore inteira (o processo raiz e todos os descendentes).</summary>
    void Kill();

    /// <summary>Espera o encerramento terminar (saída drenada e <c>onExit</c> chamado), até o tempo dado.</summary>
    bool WaitForExit(TimeSpan timeout);
}

/// <summary>Cria sessões de pseudo-console. Produção: <see cref="ConPtyFactory"/>; testes: um falso.</summary>
internal interface IPtyFactory
{
    /// <summary>Falso no Windows anterior ao 10 versão 1809 (sem ConPTY).</summary>
    bool IsSupported { get; }

    /// <param name="onOutput">Bytes da saída (UTF-8 com sequências VT), numa thread de leitura dedicada.</param>
    /// <param name="onExit">Código de saída do processo raiz, depois de toda a saída ter sido entregue.</param>
    /// <exception cref="System.ComponentModel.Win32Exception">Falha ao criar o pseudo-console ou o processo.</exception>
    IPtySession Start(PtyStartInfo info, Action<ReadOnlyMemory<byte>> onOutput, Action<int> onExit);
}
