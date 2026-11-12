namespace Monitoramento.App.Hosting;

/// <summary>Instância única por sessão do Windows: mutex nomeado + evento nomeado para trazer a janela existente à frente.</summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Monitoramento.App.Instance";
    private const string EventName = @"Local\Monitoramento.App.Activate";

    private readonly Mutex _mutex;
    private EventWaitHandle? _activate;
    private Thread? _listener;
    private volatile bool _disposed;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Devolve a posse da instância ou null quando já existe outra (que foi avisada para aparecer).</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
        if (created)
        {
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        try
        {
            Native.AllowAnyForeground();
            using var signal = EventWaitHandle.OpenExisting(EventName);
            signal.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            CrashLog.Write("Não foi possível avisar a instância em execução", ex);
        }

        return null;
    }

    public void Listen(Action onActivate)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        _activate = handle;
        _listener = new Thread(() =>
        {
            while (!_disposed)
            {
                if (handle.WaitOne(500) && !_disposed)
                {
                    onActivate();
                }
            }
        })
        {
            IsBackground = true,
            Name = "Monitoramento.Activate",
        };
        _listener.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        _listener?.Join(1000);
        _activate?.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // posse já liberada: nada a fazer
        }

        _mutex.Dispose();
    }
}
