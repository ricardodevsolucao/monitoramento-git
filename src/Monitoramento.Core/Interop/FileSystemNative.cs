using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Monitoramento.Core.Interop;

/// <summary>Chamadas de sistema de arquivos sem equivalente gerenciado (nome longo, abrir sem seguir link).</summary>
internal static unsafe partial class FileSystemNative
{
    private const uint GenericRead = 0x8000_0000;
    private const uint ShareReadWriteDelete = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FlagOpenReparsePoint = 0x0020_0000;
    private const uint FlagSequentialScan = 0x0800_0000;
    private const uint AttributeDirectory = 0x10;
    private const uint AttributeReparsePoint = 0x400;
    private const int MaxLongPath = 32_767;

    /// <summary>Nome longo de um caminho que existe (8.3 → longo); null se não existir ou falhar.</summary>
    public static string? LongPathName(string path)
    {
        uint length;
        Span<char> stack = stackalloc char[260];
        fixed (char* buffer = stack)
        {
            length = GetLongPathName(path, buffer, (uint)stack.Length);
            if (length > 0 && length < stack.Length)
            {
                return new string(buffer, 0, (int)length);
            }
        }

        if (length == 0 || length > MaxLongPath)
        {
            return null;
        }

        var heap = new char[length];
        fixed (char* buffer = heap)
        {
            var written = GetLongPathName(path, buffer, (uint)heap.Length);
            return written == 0 || written >= heap.Length ? null : new string(buffer, 0, (int)written);
        }
    }

    /// <summary>
    /// Abre um arquivo comum para leitura <b>sem seguir</b> link simbólico/junção (FILE_FLAG_OPEN_REPARSE_POINT) e
    /// confere pelo próprio handle que não é reparse point nem pasta. Devolve o handle e o tamanho, ou null.
    /// </summary>
    public static (SafeFileHandle Handle, long Size)? OpenRegularFileNoFollow(string path)
    {
        var handle = CreateFile(path, GenericRead, ShareReadWriteDelete, 0, OpenExisting, FlagOpenReparsePoint | FlagSequentialScan, 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        if (!GetFileInformationByHandle(handle, out var info)
            || (info.FileAttributes & (AttributeReparsePoint | AttributeDirectory)) != 0)
        {
            handle.Dispose();
            return null;
        }

        return (handle, ((long)info.FileSizeHigh << 32) | info.FileSizeLow);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetLongPathName(string shortPath, char* longPath, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    /// <summary>BY_HANDLE_FILE_INFORMATION; FILETIME são dois DWORD (alinhamento 4, por isso Pack = 4).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
