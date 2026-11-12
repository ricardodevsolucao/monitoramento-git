namespace Monitoramento.Core.Models;

/// <summary>Estado completo enviado à UI a cada ciclo de coleta.</summary>
public sealed record Snapshot(
    string Timestamp,
    MachineInfo Machine,
    IReadOnlyList<AppGroup> Groups,
    IReadOnlyList<SavedProcessState> Saved,
    IReadOnlyList<TerminalInfo> Terminals);

public sealed record MachineInfo(
    long TotalMemoryBytes,
    long UsedMemoryBytes,
    double MemoryPercent,
    double CpuPercent,
    int LogicalCores,
    int ProcessCount,
    IReadOnlyList<MachineHistoryPoint> History);

/// <summary>Ponto do histórico da máquina; <see cref="T"/> em epoch ms.</summary>
public sealed record MachineHistoryPoint(long T, double MemoryPercent, double CpuPercent);
