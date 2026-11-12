using System.Text;
using Monitoramento.Core.Terminal;

namespace Monitoramento.Core.Tests.TestSupport;

/// <summary>Pseudo-console falso: registra o que foi iniciado/escrito e deixa o teste produzir saída e fim.</summary>
internal sealed class FakePtyFactory : IPtyFactory
{
    private readonly Lock _gate = new();
    private readonly List<FakePtySession> _sessions = [];
    private int _nextPid = 7000;

    public bool IsSupported { get; set; } = true;

    /// <summary>Faz o próximo Start falhar com este código Win32.</summary>
    public int? FailWith { get; set; }

    public IReadOnlyList<FakePtySession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.ToList();
            }
        }
    }

    public FakePtySession Last => Sessions[^1];

    public IPtySession Start(PtyStartInfo info, Action<ReadOnlyMemory<byte>> onOutput, Action<int> onExit)
    {
        if (FailWith is { } code)
        {
            throw new System.ComponentModel.Win32Exception(code);
        }

        lock (_gate)
        {
            var session = new FakePtySession(info, _nextPid++, onOutput, onExit);
            _sessions.Add(session);
            return session;
        }
    }
}

internal sealed class FakePtySession(PtyStartInfo info, int pid, Action<ReadOnlyMemory<byte>> onOutput, Action<int> onExit) : IPtySession
{
    private readonly ManualResetEventSlim _exited = new(false);
    private readonly List<string> _written = [];

    public PtyStartInfo Info { get; } = info;

    public int Pid { get; } = pid;

    public long CreateTime { get; } = 1000 + pid;

    public (int Cols, int Rows)? LastResize { get; private set; }

    public bool Killed { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Simula descendentes vivos no Job Object depois de o raiz terminar.</summary>
    public bool HasLiveProcesses { get; set; }

    /// <summary>Kill não encerra (processo travado): para testar prazos.</summary>
    public bool IgnoreKill { get; set; }

    public IReadOnlyList<string> Written
    {
        get
        {
            lock (_written)
            {
                return _written.ToList();
            }
        }
    }

    public void Emit(string text) => EmitBytes(Encoding.UTF8.GetBytes(text));

    public void EmitBytes(byte[] bytes) => onOutput(bytes);

    public void Exit(int code)
    {
        if (_exited.IsSet)
        {
            return;
        }

        onExit(code);
        _exited.Set();
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_written)
        {
            _written.Add(Encoding.UTF8.GetString(data));
        }
    }

    public void Resize(int cols, int rows) => LastResize = (cols, rows);

    public void Kill()
    {
        Killed = true;
        HasLiveProcesses = false;
        if (!IgnoreKill)
        {
            Exit(1);
        }
    }

    public bool WaitForExit(TimeSpan timeout) => _exited.Wait(timeout);

    public void Dispose() => Disposed = true;
}
