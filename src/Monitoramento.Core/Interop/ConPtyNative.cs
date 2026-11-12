using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Monitoramento.Core.Interop;

/// <summary>
/// Pseudo-console (ConPTY), lista de atributos do processo e Job Object.
/// Fontes: https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session,
/// https://learn.microsoft.com/en-us/windows/console/createpseudoconsole,
/// https://learn.microsoft.com/en-us/windows/console/resizepseudoconsole,
/// https://learn.microsoft.com/en-us/windows/console/closepseudoconsole,
/// https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute.
/// </summary>
internal static unsafe partial class ConPtyNative
{
    /// <summary>
    /// <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c> (WinBase.h: ProcThreadAttributeValue(22, FALSE, TRUE, FALSE)); a
    /// página da Microsoft não traz o número, que é o mesmo do exemplo oficial MiniTerm (microsoft/terminal,
    /// samples/ConPTY/MiniTerm/MiniTerm/Native/PseudoConsoleApi.cs).
    /// </summary>
    public const nint ProcThreadAttributePseudoConsole = 0x0002_0016;

    public const uint ExtendedStartupInfoPresent = 0x0008_0000;
    public const uint CreateSuspended = 0x0000_0004;
    public const uint StartfUseStdHandles = 0x0000_0100;
    public const int JobObjectBasicAccountingInformation = 1;
    public const int JobObjectExtendedLimitInformation = 9;
    public const uint JobObjectLimitKillOnJobClose = 0x0000_2000;
    public const uint Infinite = 0xFFFF_FFFF;

    /// <summary>ConPTY existe neste Windows (10 1809 ou mais recente)?</summary>
    public static bool IsAvailable()
    {
        try
        {
            return NativeLibrary.TryLoad("kernel32.dll", out var kernel32)
                && NativeLibrary.TryGetExport(kernel32, "CreatePseudoConsole", out _)
                && NativeLibrary.TryGetExport(kernel32, "ResizePseudoConsole", out _)
                && NativeLibrary.TryGetExport(kernel32, "ClosePseudoConsole", out _);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, nint pipeAttributes, uint size);

    [LibraryImport("kernel32.dll")]
    public static partial int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint pseudoConsole);

    [LibraryImport("kernel32.dll")]
    public static partial int ResizePseudoConsole(nint pseudoConsole, Coord size);

    [LibraryImport("kernel32.dll")]
    public static partial void ClosePseudoConsole(nint pseudoConsole);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitializeProcThreadAttributeList(nint attributeList, int attributeCount, int flags, ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateProcThreadAttribute(nint attributeList, uint flags, nint attribute, nint value, nuint size, nint previousValue, nint returnSize);

    [LibraryImport("kernel32.dll")]
    public static partial void DeleteProcThreadAttributeList(nint attributeList);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    public static partial SafeJobHandle CreateJobObject(nint jobAttributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(SafeJobHandle job, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryInformationJobObject(SafeJobHandle job, int infoClass, void* info, uint length, out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    /// <summary>COORD.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Coord(short x, short y)
    {
        public short X = x;
        public short Y = y;
    }

    /// <summary>STARTUPINFOEXW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoEx
    {
        public NativeMethods.StartupInfo StartupInfo;
        public nint AttributeList;
    }

    /// <summary>JOBOBJECT_BASIC_ACCOUNTING_INFORMATION.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct JobBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    /// <summary>JOBOBJECT_EXTENDED_LIMIT_INFORMATION (x64, alinhamento natural).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct JobExtendedLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}

/// <summary>Handle de Job Object (fechado com CloseHandle).</summary>
internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}
