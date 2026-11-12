using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Monitoramento.Core.Interop;

/// <summary>Declarações Win32/NT usadas pela coleta. Todas source-generated (LibraryImport).</summary>
internal static unsafe partial class NativeMethods
{
    public const int SystemProcessInformation = 5;
    public const int ProcessBasicInformation = 0;
    public const int ProcessWow64Information = 26;
    public const int ProcessCommandLineInformation = 60;

    public const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    public const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    public const int StatusBufferOverflow = unchecked((int)0x80000005);

    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint ProcessVmRead = 0x0010;
    public const uint ProcessTerminate = 0x0001;
    public const uint Synchronize = 0x0010_0000;

    public const uint CreateNewConsole = 0x0000_0010;
    public const uint CreateUnicodeEnvironment = 0x0000_0400;
    public const uint CreateNoWindow = 0x0800_0000;

    public const int ErrorInvalidParameter = 87;
    public const uint WaitObject0 = 0;

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int systemInformationClass, void* systemInformation, uint length, out uint returnLength);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryInformationProcess(SafeProcessHandle process, int processInformationClass, void* processInformation, uint length, out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadProcessMemory(SafeProcessHandle process, nint baseAddress, void* buffer, nuint size, out nuint bytesRead);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* exeName, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(SafeProcessHandle process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcess(
        char* applicationName,
        char* commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        char* environment,
        char* currentDirectory,
        StartupInfo* startupInfo,
        ProcessInformation* processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    /// <summary>STARTUPINFOW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfo
    {
        public uint Cb;
        public char* Reserved;
        public char* Desktop;
        public char* Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public byte* Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    /// <summary>PROCESS_INFORMATION.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    /// <summary>Início de SYSTEM_PROCESS_INFORMATION (x64) até InheritedFromUniqueProcessId.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SystemProcessInformationHeader
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public ushort ImageNameLength;
        public ushort ImageNameMaximumLength;
        public nint ImageNameBuffer;
        public int BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UnicodeString64
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    /// <summary>UNICODE_STRING de processo 32 bits (WOW64): ponteiro de 4 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct UnicodeString32
    {
        public ushort Length;
        public ushort MaximumLength;
        public uint Buffer;
    }
}
