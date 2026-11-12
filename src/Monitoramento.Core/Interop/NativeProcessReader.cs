using System.Buffers;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using Monitoramento.Core.Collection;

namespace Monitoramento.Core.Interop;

/// <summary>
/// Lê caminho do executável, linha de comando e pasta de trabalho de outro processo.
/// Linha de comando: <c>ProcessCommandLineInformation</c> (só exige QUERY_LIMITED, funciona para 32 e 64 bits).
/// Pasta de trabalho: PEB → RTL_USER_PROCESS_PARAMETERS.CurrentDirectory via ReadProcessMemory (PEB64 ou PEB32 em WOW64).
/// Nunca lança exceção: sem acesso devolve nulos e <see cref="ProcessDetails.AccessDenied"/>; abriu mas leu nulo,
/// <see cref="ProcessDetails.Incomplete"/>.
/// </summary>
internal sealed unsafe class NativeProcessReader : IProcessReader
{
    // Offsets x64
    private const int Peb64ProcessParameters = 0x20;
    private const int Params64CurrentDirectory = 0x38;

    // Offsets x86 (WOW64)
    private const int Peb32ProcessParameters = 0x10;
    private const int Params32CurrentDirectory = 0x24;

    private const int MaxPathChars = 32_767;

    public ProcessDetails Read(int pid)
    {
        if (pid <= 0)
        {
            return new ProcessDetails(null, null, null, AccessDenied: true);
        }

        using var full = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation | NativeMethods.ProcessVmRead, inheritHandle: false, pid);
        if (!full.IsInvalid)
        {
            var exe = QueryImagePath(full);
            var commandLine = QueryCommandLine(full);
            var cwd = ReadWorkingDirectory(full);
            // abriu com leitura: nulos aqui são de processo ainda inicializando, não falta de acesso
            return new ProcessDetails(exe, commandLine, cwd, AccessDenied: false, Incomplete: commandLine is null || cwd is null);
        }

        using var limited = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, inheritHandle: false, pid);
        if (limited.IsInvalid)
        {
            return new ProcessDetails(null, null, null, AccessDenied: true);
        }

        return new ProcessDetails(QueryImagePath(limited), QueryCommandLine(limited), null, AccessDenied: true);
    }

    public string? ReadWorkingDirectory(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        using var handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation | NativeMethods.ProcessVmRead, inheritHandle: false, pid);
        return handle.IsInvalid ? null : ReadWorkingDirectory(handle);
    }

    private static string? QueryImagePath(SafeProcessHandle handle)
    {
        var chars = stackalloc char[1024];
        uint size = 1024;
        if (NativeMethods.QueryFullProcessImageName(handle, 0, chars, ref size))
        {
            return new string(chars, 0, (int)size);
        }

        var rented = ArrayPool<char>.Shared.Rent(MaxPathChars);
        try
        {
            fixed (char* big = rented)
            {
                size = (uint)rented.Length;
                return NativeMethods.QueryFullProcessImageName(handle, 0, big, ref size) ? new string(big, 0, (int)size) : null;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static string? QueryCommandLine(SafeProcessHandle handle)
    {
        var length = 4096;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                fixed (byte* buffer = rented)
                {
                    var status = NativeMethods.NtQueryInformationProcess(
                        handle, NativeMethods.ProcessCommandLineInformation, buffer, (uint)rented.Length, out var needed);

                    if (status is NativeMethods.StatusInfoLengthMismatch or NativeMethods.StatusBufferTooSmall or NativeMethods.StatusBufferOverflow)
                    {
                        length = (int)Math.Max(needed, (uint)rented.Length * 2);
                        continue;
                    }

                    if (status < 0)
                    {
                        return null;
                    }

                    // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; } seguido dos caracteres
                    var bytes = Unsafe.ReadUnaligned<ushort>(buffer);
                    var text = Unsafe.ReadUnaligned<nint>(buffer + nint.Size);
                    return bytes == 0 || text == 0 ? string.Empty : new string((char*)text, 0, bytes / sizeof(char));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        return null;
    }

    private static string? ReadWorkingDirectory(SafeProcessHandle handle)
    {
        if (!Environment.Is64BitProcess)
        {
            return null; // leitor 32 bits não lê PEB de processo 64 bits
        }

        nint peb32 = 0;
        if (NativeMethods.NtQueryInformationProcess(handle, NativeMethods.ProcessWow64Information, &peb32, (uint)sizeof(nint), out _) < 0)
        {
            peb32 = 0;
        }

        return peb32 != 0 ? ReadCwd32(handle, peb32) : ReadCwd64(handle);
    }

    private static string? ReadCwd64(SafeProcessHandle handle)
    {
        var basic = stackalloc nint[6]; // PROCESS_BASIC_INFORMATION: ExitStatus, PebBaseAddress, ...
        if (NativeMethods.NtQueryInformationProcess(handle, NativeMethods.ProcessBasicInformation, basic, (uint)(sizeof(nint) * 6), out _) < 0)
        {
            return null;
        }

        var peb = basic[1];
        if (peb == 0 || !TryRead(handle, peb + Peb64ProcessParameters, out nint parameters) || parameters == 0)
        {
            return null;
        }

        if (!TryRead(handle, parameters + Params64CurrentDirectory, out NativeMethods.UnicodeString64 dosPath))
        {
            return null;
        }

        return ReadString(handle, dosPath.Buffer, dosPath.Length);
    }

    private static string? ReadCwd32(SafeProcessHandle handle, nint peb32)
    {
        if (!TryRead(handle, peb32 + Peb32ProcessParameters, out uint parameters) || parameters == 0)
        {
            return null;
        }

        if (!TryRead(handle, (nint)parameters + Params32CurrentDirectory, out NativeMethods.UnicodeString32 dosPath))
        {
            return null;
        }

        return ReadString(handle, (nint)dosPath.Buffer, dosPath.Length);
    }

    private static string? ReadString(SafeProcessHandle handle, nint address, ushort byteLength)
    {
        if (address == 0 || byteLength == 0 || byteLength % 2 != 0)
        {
            return null;
        }

        var chars = stackalloc char[byteLength / sizeof(char)];
        if (!NativeMethods.ReadProcessMemory(handle, address, chars, byteLength, out var read) || read != byteLength)
        {
            return null;
        }

        return TrimTrailingSeparator(new ReadOnlySpan<char>(chars, byteLength / sizeof(char)));
    }

    /// <summary>O PEB guarda "C:\pasta\"; devolvemos "C:\pasta" (mas mantemos "C:\").</summary>
    private static string TrimTrailingSeparator(ReadOnlySpan<char> path)
    {
        while (path.Length > 3 && path[^1] == '\\')
        {
            path = path[..^1];
        }

        return path.ToString();
    }

    private static bool TryRead<T>(SafeProcessHandle handle, nint address, out T value)
        where T : unmanaged
    {
        T local = default;
        var ok = NativeMethods.ReadProcessMemory(handle, address, &local, (nuint)sizeof(T), out var read) && read == (nuint)sizeof(T);
        value = local;
        return ok;
    }
}
