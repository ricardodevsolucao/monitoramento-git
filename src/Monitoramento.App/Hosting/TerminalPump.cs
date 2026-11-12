using Monitoramento.Core.Bridge;

namespace Monitoramento.App.Hosting;

/// <summary>
/// Recolhe a saída dos terminais embutidos a cada 30 ms e entrega as mensagens <c>termOutput</c>/<c>termExit</c> (já em
/// lotes de até 32 Ki caracteres) para o host postar. Com contrapressão: só recolhe o próximo lote depois de o anterior
/// ter sido postado (a UI lenta não acumula mensagens na fila da janela; a saída espera no gerenciador, que tem teto).
/// Roda sempre: sem WebView o host descarta, e o anel de cada terminal guarda o recente para o <c>termAttach</c>.
/// </summary>
internal sealed class TerminalPump : IDisposable
{
    public const int IntervalMs = 30;

    private readonly BridgeRouter _router;
    private readonly Func<IReadOnlyList<string>, Task> _publish;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <param name="publish">Posta o lote; a tarefa termina quando ele foi entregue (ou descartado).</param>
    public TerminalPump(BridgeRouter router, Func<IReadOnlyList<string>, Task> publish)
    {
        _router = router;
        _publish = publish;
    }

    public void Start() => _loop = Task.Factory.StartNew(
        RunAsync, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task RunAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var messages = _router.TakeTerminalMessages();
                if (messages.Count > 0)
                {
                    await _publish(messages).WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (TimeoutException)
            {
                // a janela não postou em 5 s (travada/encerrando): segue; a UI recupera com termAttach
            }
            catch (Exception ex)
            {
                CrashLog.Write("Falha ao entregar a saída dos terminais", ex);
            }

            try
            {
                await Task.Delay(IntervalMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // cancelamento esperado
        }

        _cts.Dispose();
    }
}
