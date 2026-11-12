using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Monitoramento.Core.Interop;

namespace Monitoramento.Core.Actions;

/// <summary>Resultado de uma ação: valor, ou mensagem de erro em português.</summary>
/// <param name="Code">Código estável do erro para a UI reagir (ex.: "unsafeRepo"), além da mensagem; normalmente null.</param>
internal sealed record OperationResult<T>(T? Value, string? Error, string? Code = null)
{
    public bool Ok => Error is null;

    public static OperationResult<T> Success(T value) => new(value, null);

    public static OperationResult<T> Failure(string error, string? code = null) => new(default, error, code);
}

/// <summary>Processo iniciado: pid e CreateTime (FILETIME UTC), suficientes para rastrear a árvore.</summary>
internal sealed record StartedProcess(int Pid, long CreateTime);

/// <summary>
/// Início de um processo salvo: <paramref name="CommandLine"/> completa (já com o executável entre aspas),
/// pasta, variáveis acrescentadas ao ambiente do app e se abre console novo (visível) ou nenhum (oculto).
/// </summary>
internal sealed record LaunchSpec(
    string ApplicationName,
    string CommandLine,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    bool NewConsole,
    string Title);

internal interface IProcessStarter
{
    /// <summary>Ações de pasta (Explorer/terminal).</summary>
    StartedProcess Start(ProcessStartInfo startInfo);

    /// <summary>Processo salvo: o pid devolvido é o do próprio processo criado (sem intermediário).</summary>
    StartedProcess Launch(LaunchSpec spec);
}

/// <summary>Handle aberto de um processo: identidade conferida e encerramento pelo mesmo handle.</summary>
internal interface IProcessHandle : IDisposable
{
    /// <summary>CreateTime atual (FILETIME UTC); 0 se não foi possível ler.</summary>
    long CreateTime { get; }

    /// <summary>Nome atual da imagem (ex.: "node.exe"), ou null.</summary>
    string? Name { get; }

    /// <exception cref="Win32Exception">Sem permissão para encerrar.</exception>
    void Terminate();
}

internal interface IProcessKiller
{
    /// <returns>Handle do processo, ou null se ele não existe mais.</returns>
    /// <exception cref="Win32Exception">Acesso negado.</exception>
    IProcessHandle? Open(int pid);
}

internal sealed unsafe class SystemProcessStarter : IProcessStarter
{
    public StartedProcess Start(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new StartedProcess(0, 0);
        }

        try
        {
            return new StartedProcess(process.Id, process.StartTime.ToFileTimeUtc());
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return new StartedProcess(process.Id, 0);
        }
    }

    /// <summary>
    /// <c>CreateProcessW</c> direto: <c>CREATE_NEW_CONSOLE</c> (visível) ou <c>CREATE_NO_WINDOW</c> (oculto), sem herdar handles.
    /// O CreateTime vem de <c>GetProcessTimes</c> no handle devolvido, igual ao da coleta.
    /// </summary>
    public StartedProcess Launch(LaunchSpec spec)
    {
        var flags = NativeMethods.CreateUnicodeEnvironment | (spec.NewConsole ? NativeMethods.CreateNewConsole : NativeMethods.CreateNoWindow);
        var commandLine = (spec.CommandLine + '\0').ToCharArray(); // CreateProcessW pode alterar o buffer
        var environment = BuildEnvironmentBlock(spec.Environment);

        NativeMethods.ProcessInformation info;
        fixed (char* application = spec.ApplicationName)
        fixed (char* command = commandLine)
        fixed (char* env = environment)
        fixed (char* directory = spec.WorkingDirectory)
        fixed (char* title = spec.NewConsole ? spec.Title : null)
        {
            var startup = new NativeMethods.StartupInfo { Cb = (uint)sizeof(NativeMethods.StartupInfo), Title = title };
            if (!NativeMethods.CreateProcess(application, command, 0, 0, false, flags, env, directory, &startup, &info))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        NativeMethods.CloseHandle(info.Thread);
        using var process = new SafeProcessHandle(info.Process, ownsHandle: true);
        var created = NativeMethods.GetProcessTimes(process, out var creation, out _, out _, out _) ? creation : 0;
        return new StartedProcess((int)info.ProcessId, created);
    }

    /// <summary>Ambiente do app + variáveis do processo salvo (elas vencem), ordenado e terminado em "\0\0".</summary>
    internal static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> extra)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && IsValidName(key))
            {
                variables[key] = entry.Value as string ?? string.Empty;
            }
        }

        foreach (var (key, value) in extra)
        {
            if (IsValidName(key))
            {
                variables[key] = value.Replace("\0", string.Empty, StringComparison.Ordinal);
            }
        }

        var block = new StringBuilder();
        foreach (var (key, value) in variables)
        {
            block.Append(key).Append('=').Append(value).Append('\0');
        }

        return block.Append('\0').ToString();
    }

    private static bool IsValidName(string key) =>
        key.Length > 0 && !key.Contains('=', StringComparison.Ordinal) && !key.Contains('\0', StringComparison.Ordinal);
}

/// <summary>Abre o processo com TERMINATE|QUERY_LIMITED|SYNCHRONIZE; identidade e encerramento no mesmo handle.</summary>
internal sealed class SystemProcessKiller : IProcessKiller
{
    public IProcessHandle? Open(int pid)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessTerminate | NativeMethods.ProcessQueryLimitedInformation | NativeMethods.Synchronize,
            inheritHandle: false,
            pid);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error == NativeMethods.ErrorInvalidParameter)
            {
                return null; // pid inexistente
            }

            throw new Win32Exception(error);
        }

        if (NativeMethods.WaitForSingleObject(handle, 0) == NativeMethods.WaitObject0)
        {
            handle.Dispose(); // já terminou (handle ainda aberto por alguém)
            return null;
        }

        return new Handle(handle);
    }

    private sealed unsafe class Handle : IProcessHandle
    {
        private readonly SafeProcessHandle _handle;

        public Handle(SafeProcessHandle handle)
        {
            _handle = handle;
            CreateTime = NativeMethods.GetProcessTimes(handle, out var created, out _, out _, out _) ? created : 0;
            Name = ReadName(handle);
        }

        public long CreateTime { get; }

        public string? Name { get; }

        public void Terminate()
        {
            if (!NativeMethods.TerminateProcess(_handle, 1))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        public void Dispose() => _handle.Dispose();

        private static string? ReadName(SafeProcessHandle handle)
        {
            var chars = stackalloc char[1024];
            uint size = 1024;
            return NativeMethods.QueryFullProcessImageName(handle, 0, chars, ref size)
                ? Path.GetFileName(new string(chars, 0, (int)size))
                : null;
        }
    }
}
