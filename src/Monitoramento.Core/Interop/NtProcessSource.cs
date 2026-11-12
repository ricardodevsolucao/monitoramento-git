using System.Runtime.CompilerServices;
using Monitoramento.Core.Collection;

namespace Monitoramento.Core.Interop;

/// <summary>
/// Lista todos os processos com uma chamada a <c>NtQuerySystemInformation(SystemProcessInformation)</c>.
/// Funciona inclusive para processos elevados/protegidos, pois não abre handle de nenhum processo.
/// Falha da chamada devolve null (não lista vazia), para não ser confundida com "nenhum processo".
/// </summary>
internal sealed unsafe class NtProcessSource : IProcessSource
{
    private const int InitialBufferSize = 512 * 1024;

    private readonly Lock _gate = new();
    private byte[] _buffer = new byte[InitialBufferSize];

    public IReadOnlyList<RawProcess>? ReadAll()
    {
        if (!Environment.Is64BitProcess)
        {
            return [];
        }

        lock (_gate)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                fixed (byte* buffer = _buffer)
                {
                    var status = NativeMethods.NtQuerySystemInformation(
                        NativeMethods.SystemProcessInformation, buffer, (uint)_buffer.Length, out var needed);

                    if (status == NativeMethods.StatusInfoLengthMismatch)
                    {
                        _buffer = new byte[Math.Max(_buffer.Length * 2, (int)needed + (64 * 1024))];
                        continue;
                    }

                    return status < 0 ? null : Parse(buffer, _buffer.Length);
                }
            }

            return null;
        }
    }

    private static List<RawProcess> Parse(byte* buffer, int length)
    {
        var result = new List<RawProcess>(400);
        var offset = 0;

        while (offset + sizeof(NativeMethods.SystemProcessInformationHeader) <= length)
        {
            var info = Unsafe.ReadUnaligned<NativeMethods.SystemProcessInformationHeader>(buffer + offset);
            var pid = (int)info.UniqueProcessId;
            var name = info.ImageNameLength > 0 && info.ImageNameBuffer != 0
                ? new string((char*)info.ImageNameBuffer, 0, info.ImageNameLength / sizeof(char))
                : pid == 0 ? "Idle" : string.Empty;

            result.Add(new RawProcess(
                pid,
                (int)info.InheritedFromUniqueProcessId,
                name,
                info.WorkingSetPrivateSize,
                info.KernelTime + info.UserTime,
                info.CreateTime));

            if (info.NextEntryOffset == 0)
            {
                break;
            }

            offset += (int)info.NextEntryOffset;
        }

        return result;
    }
}
