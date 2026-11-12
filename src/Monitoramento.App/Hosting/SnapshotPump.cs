using Monitoramento.Core;
using Monitoramento.Core.Bridge;
using Monitoramento.Core.Models;

namespace Monitoramento.App.Hosting;

/// <summary>
/// Laço de coleta em segundo plano. Com a janela visível amostra a cada <c>refreshIntervalMs</c> (relido a cada ciclo)
/// e publica o snapshot; escondida/minimizada, amostra só a cada 10 s (bandeja e histórico) e não publica.
/// </summary>
internal sealed class SnapshotPump : IDisposable
{
    public const int IdleIntervalMs = 10_000;

    private readonly MonitorEngine _engine;
    private readonly BridgeRouter _router;
    private readonly Func<bool> _isActive;
    private readonly Action<string> _publish;
    private readonly Action<Snapshot> _sampled;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _loop;

    public SnapshotPump(MonitorEngine engine, BridgeRouter router, Func<bool> isActive, Action<string> publish, Action<Snapshot> sampled)
    {
        _engine = engine;
        _router = router;
        _isActive = isActive;
        _publish = publish;
        _sampled = sampled;
    }

    public void Start() => _loop = Task.Factory.StartNew(
        RunAsync, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    /// <summary>Interrompe a espera atual e amostra já (janela reaberta, configuração alterada).</summary>
    public void WakeUp()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // já há um despertar pendente
        }
        catch (ObjectDisposedException)
        {
            // encerrando
        }
    }

    private async Task RunAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            var active = _isActive();
            try
            {
                var snapshot = _engine.Sample();
                if (active)
                {
                    _publish(_router.BuildSnapshotMessage(snapshot));
                }

                _sampled(snapshot);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                CrashLog.Write("Falha na coleta", ex);
            }

            var delay = active
                ? Math.Clamp(_engine.Config.Settings.RefreshIntervalMs, Settings.MinRefreshIntervalMs, Settings.MaxRefreshIntervalMs)
                : IdleIntervalMs;
            try
            {
                await _wake.WaitAsync(delay, token).ConfigureAwait(false);
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
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // cancelamento esperado
        }

        _cts.Dispose();
        _wake.Dispose();
    }
}
