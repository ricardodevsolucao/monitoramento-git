using System.Runtime.InteropServices;
using Monitoramento.Core.Collection;

namespace Monitoramento.Core.Interop;

/// <summary>Memória física (GlobalMemoryStatusEx) e tempos de CPU da máquina (GetSystemTimes).</summary>
internal sealed class NativeSystemInfoSource : ISystemInfoSource
{
    public int LogicalCores { get; } = Environment.ProcessorCount;

    public MemoryInfo ReadMemory()
    {
        var status = new NativeMethods.MemoryStatusEx { Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
        return NativeMethods.GlobalMemoryStatusEx(ref status)
            ? new MemoryInfo((long)status.TotalPhys, (long)status.AvailPhys)
            : default;
    }

    public SystemCpuTimes ReadCpuTimes() =>
        NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user)
            ? new SystemCpuTimes(idle, kernel, user)
            : default;
}
