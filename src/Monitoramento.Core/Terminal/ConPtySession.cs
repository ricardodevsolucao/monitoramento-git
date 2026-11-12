using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Monitoramento.Core.Actions;
using Monitoramento.Core.Interop;

namespace Monitoramento.Core.Terminal;

/// <summary>Fábrica de produção: ConPTY do Windows.</summary>
internal sealed class ConPtyFactory : IPtyFactory
{
    private readonly Lazy<bool> _supported = new(ConPtyNative.IsAvailable);

    public bool IsSupported => _supported.Value;

    public IPtySession Start(PtyStartInfo info, Action<ReadOnlyMemory<byte>> onOutput, Action<int> onExit) =>
        ConPtySession.Start(info, onOutput, onExit);
}

/// <summary>
/// Uma sessão ConPTY, seguindo "Creating a Pseudoconsole session"
/// (https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session): dois pipes síncronos,
/// <c>CreatePseudoConsole</c> com a ponta de leitura da entrada e a de escrita da saída, <c>STARTUPINFOEX</c> com
/// <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c> e <c>CreateProcess</c> com <c>EXTENDED_STARTUPINFO_PRESENT</c>; depois do
/// CreateProcess as pontas entregues ao pseudo-console são fechadas aqui. Cada canal tem a sua thread (a doc recomenda,
/// para evitar deadlock), e o <c>ClosePseudoConsole</c> roda numa thread diferente da que lê a saída, que continua
/// drenando até o pipe quebrar (https://learn.microsoft.com/en-us/windows/console/closepseudoconsole).
/// A árvore inteira fica num Job Object com KILL_ON_JOB_CLOSE: encerrar o terminal (ou o app) mata todos os descendentes.
/// </summary>
internal sealed unsafe class ConPtySession : IPtySession
{
    private const int ReadBufferBytes = 16 * 1024;
    private static readonly TimeSpan ReaderDrainWait = TimeSpan.FromSeconds(5);

    private readonly nint _pseudoConsole;
    private readonly SafeFileHandle _outputRead;
    private readonly FileStream _input;
    private readonly SafeProcessHandle _process;
    private readonly SafeJobHandle? _job;
    private readonly Action<ReadOnlyMemory<byte>> _onOutput;
    private readonly Action<int> _onExit;
    private readonly ManualResetEventSlim _exited = new(false);
    private readonly Lock _gate = new();
    private readonly Lock _writeGate = new();
    private readonly Thread _reader;
    private readonly Thread _waiter;
    private bool _pseudoConsoleClosed;
    private bool _disposed;

    private ConPtySession(
        nint pseudoConsole,
        SafeFileHandle inputWrite,
        SafeFileHandle outputRead,
        SafeProcessHandle process,
        SafeJobHandle? job,
        int pid,
        long createTime,
        Action<ReadOnlyMemory<byte>> onOutput,
        Action<int> onExit)
    {
        _pseudoConsole = pseudoConsole;
        _input = new FileStream(inputWrite, FileAccess.Write, bufferSize: 0, isAsync: false);
        _outputRead = outputRead;
        _process = process;
        _job = job;
        Pid = pid;
        CreateTime = createTime;
        _onOutput = onOutput;
        _onExit = onExit;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"Terminal {pid} (saída)" };
        _waiter = new Thread(WaitLoop) { IsBackground = true, Name = $"Terminal {pid} (fim)" };
    }

    public int Pid { get; }

    public long CreateTime { get; }

    public static ConPtySession Start(PtyStartInfo info, Action<ReadOnlyMemory<byte>> onOutput, Action<int> onExit)
    {
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        SafeJobHandle? job = null;
        SafeProcessHandle? processHandle = null;
        var pseudoConsole = nint.Zero;
        var attributeList = nint.Zero;
        var success = false;
        try
        {
            if (!ConPtyNative.CreatePipe(out inputRead, out inputWrite, 0, 0) || !ConPtyNative.CreatePipe(out outputRead, out outputWrite, 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var size = new ConPtyNative.Coord((short)info.Cols, (short)info.Rows);
            var hr = ConPtyNative.CreatePseudoConsole(size, inputRead, outputWrite, 0, out pseudoConsole);
            if (hr < 0)
            {
                throw new Win32Exception(hr);
            }

            attributeList = CreateAttributeList(pseudoConsole);
            job = CreateKillOnCloseJob();

            var startup = new ConPtyNative.StartupInfoEx { AttributeList = attributeList };
            startup.StartupInfo.Cb = (uint)sizeof(ConPtyNative.StartupInfoEx);

            // Sem herdar os handles padrão do app (ex.: saída redirecionada do host de testes): o filho usa só o
            // pseudo-console. (Não está na doc da Microsoft; conferido no teste de integração com saída redirecionada.)
            startup.StartupInfo.Flags = ConPtyNative.StartfUseStdHandles;

            var flags = ConPtyNative.ExtendedStartupInfoPresent | NativeMethods.CreateUnicodeEnvironment | ConPtyNative.CreateSuspended;
            var commandLine = (info.CommandLine + '\0').ToCharArray(); // CreateProcessW pode alterar o buffer
            var environment = SystemProcessStarter.BuildEnvironmentBlock(info.Environment);

            NativeMethods.ProcessInformation process;
            fixed (char* application = info.ApplicationName)
            fixed (char* command = commandLine)
            fixed (char* env = environment)
            fixed (char* directory = info.WorkingDirectory)
            {
                if (!NativeMethods.CreateProcess(application, command, 0, 0, false, flags, env, directory, &startup.StartupInfo, &process))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }

            processHandle = new SafeProcessHandle(process.Process, ownsHandle: true);
            if (job is not null && !ConPtyNative.AssignProcessToJobObject(job, processHandle))
            {
                Trace.TraceWarning($"Monitoramento: terminal fora do Job Object (erro {Marshal.GetLastPInvokeError()}).");
                job.Dispose();
                job = null;
            }

            ConPtyNative.ResumeThread(process.Thread);
            NativeMethods.CloseHandle(process.Thread);
            var created = NativeMethods.GetProcessTimes(processHandle, out var creation, out _, out _, out _) ? creation : 0;

            // a doc: depois do CreateProcess, liberar as pontas entregues ao pseudo-console
            inputRead.Dispose();
            outputWrite.Dispose();

            var session = new ConPtySession(pseudoConsole, inputWrite, outputRead, processHandle, job, (int)process.ProcessId, created, onOutput, onExit);
            session._reader.Start();
            session._waiter.Start();
            success = true;
            return session;
        }
        finally
        {
            if (attributeList != nint.Zero)
            {
                ConPtyNative.DeleteProcThreadAttributeList(attributeList);
                NativeMemory.Free((void*)attributeList);
            }

            if (!success)
            {
                // ordem: o Job (mata a árvore, se já criada), o processo, os pipes e por último o pseudo-console — com
                // os pipes fechados o ClosePseudoConsole não fica esperando alguém drenar a saída
                job?.Dispose();
                if (processHandle is { IsInvalid: false })
                {
                    NativeMethods.TerminateProcess(processHandle, 1);
                }

                processHandle?.Dispose();
                inputRead?.Dispose();
                inputWrite?.Dispose();
                outputRead?.Dispose();
                outputWrite?.Dispose();
                if (pseudoConsole != nint.Zero)
                {
                    ConPtyNative.ClosePseudoConsole(pseudoConsole);
                }
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        // lock próprio: uma escrita bloqueada no pipe nunca segura o fechamento do pseudo-console
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            _input.Write(data);
            _input.Flush();
        }
    }

    /// <summary>Processos vivos no Job Object (descendentes que sobreviveram ao raiz contam).</summary>
    public bool HasLiveProcesses
    {
        get
        {
            if (_job is null || _job.IsClosed)
            {
                return !_exited.IsSet;
            }

            var info = default(ConPtyNative.JobBasicAccountingInformation);
            return ConPtyNative.QueryInformationJobObject(_job, ConPtyNative.JobObjectBasicAccountingInformation, &info, (uint)sizeof(ConPtyNative.JobBasicAccountingInformation), out _)
                ? info.ActiveProcesses > 0
                : !_exited.IsSet;
        }
    }

    public void Resize(int cols, int rows)
    {
        lock (_gate)
        {
            if (_disposed || _pseudoConsoleClosed)
            {
                return;
            }

            var hr = ConPtyNative.ResizePseudoConsole(_pseudoConsole, new ConPtyNative.Coord((short)cols, (short)rows));
            if (hr < 0)
            {
                throw new Win32Exception(hr);
            }
        }
    }

    /// <summary>Encerra a árvore inteira, inclusive descendentes que sobreviveram ao processo raiz.</summary>
    public void Kill()
    {
        if (_job is { IsClosed: false } && ConPtyNative.TerminateJobObject(_job, 1))
        {
            return;
        }

        if (!_exited.IsSet)
        {
            NativeMethods.TerminateProcess(_process, 1);
        }
    }

    public bool WaitForExit(TimeSpan timeout) => _exited.Wait(timeout);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Kill();
        if (!_exited.Wait(TimeSpan.FromSeconds(3)))
        {
            ClosePseudoConsoleOnce(); // destrava o leitor se o processo não terminou
        }

        lock (_writeGate)
        {
            _input.Dispose();
        }

        _job?.Dispose(); // KILL_ON_JOB_CLOSE: o que restar da árvore morre aqui
        if (_exited.IsSet)
        {
            _process.Dispose();
            _outputRead.Dispose();
        }
    }

    private static nint CreateAttributeList(nint pseudoConsole)
    {
        nuint bytes = 0;
        ConPtyNative.InitializeProcThreadAttributeList(0, 1, 0, ref bytes); // 1ª chamada: só o tamanho (falha esperada)
        var list = (nint)NativeMemory.Alloc(bytes);
        if (!ConPtyNative.InitializeProcThreadAttributeList(list, 1, 0, ref bytes))
        {
            var error = Marshal.GetLastPInvokeError();
            NativeMemory.Free((void*)list);
            throw new Win32Exception(error);
        }

        // como na doc e no exemplo oficial: o valor é o próprio HPCON, com cbSize = sizeof(HPCON)
        if (!ConPtyNative.UpdateProcThreadAttribute(list, 0, ConPtyNative.ProcThreadAttributePseudoConsole, pseudoConsole, (nuint)nint.Size, 0, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            ConPtyNative.DeleteProcThreadAttributeList(list);
            NativeMemory.Free((void*)list);
            throw new Win32Exception(error);
        }

        return list;
    }

    private static SafeJobHandle? CreateKillOnCloseJob()
    {
        var job = ConPtyNative.CreateJobObject(0, 0);
        if (job.IsInvalid)
        {
            job.Dispose();
            return null;
        }

        var limits = new ConPtyNative.JobExtendedLimitInformation { LimitFlags = ConPtyNative.JobObjectLimitKillOnJobClose };
        if (!ConPtyNative.SetInformationJobObject(job, ConPtyNative.JobObjectExtendedLimitInformation, &limits, (uint)sizeof(ConPtyNative.JobExtendedLimitInformation)))
        {
            job.Dispose();
            return null;
        }

        return job;
    }

    private void ReadLoop()
    {
        var buffer = new byte[ReadBufferBytes];
        try
        {
            using var stream = new FileStream(_outputRead, FileAccess.Read, bufferSize: 0, isAsync: false);
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return; // pipe fechado: pseudo-console encerrado
                }

                _onOutput(buffer.AsMemory(0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // pipe quebrado: fim normal
        }
    }

    private void WaitLoop()
    {
        NativeMethods.WaitForSingleObject(_process, ConPtyNative.Infinite);
        var exitCode = ConPtyNative.GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : -1;

        // fecha o pseudo-console fora da thread de leitura, que segue drenando o último quadro até o pipe quebrar
        ClosePseudoConsoleOnce();
        _reader.Join(ReaderDrainWait);
        try
        {
            _onExit(exitCode);
        }
        finally
        {
            _exited.Set();
        }
    }

    private void ClosePseudoConsoleOnce()
    {
        // no lock: o Resize nunca usa o HPCON já fechado (a leitura da saída não usa este lock e segue drenando)
        lock (_gate)
        {
            if (_pseudoConsoleClosed)
            {
                return;
            }

            _pseudoConsoleClosed = true;
            ConPtyNative.ClosePseudoConsole(_pseudoConsole);
        }
    }
}
