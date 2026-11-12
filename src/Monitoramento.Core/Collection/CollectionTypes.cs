namespace Monitoramento.Core.Collection;

/// <summary>
/// Um processo como vem de <c>NtQuerySystemInformation</c>.
/// <paramref name="CpuTime"/> e <paramref name="CreateTime"/> em unidades de 100 ns (CreateTime = FILETIME UTC).
/// </summary>
internal readonly record struct RawProcess(int Pid, int ParentPid, string Name, long PrivateBytes, long CpuTime, long CreateTime);

/// <summary>
/// Dados lidos com handle do processo. <paramref name="AccessDenied"/>: não foi possível abrir o processo com leitura.
/// <paramref name="Incomplete"/>: abriu, mas linha de comando ou pasta vieram nulas (processo recém-criado ainda sem
/// ProcessParameters) — vale tentar de novo.
/// </summary>
internal sealed record ProcessDetails(string? ExePath, string? CommandLine, string? WorkingDirectory, bool AccessDenied, bool Incomplete = false);

internal readonly record struct MemoryInfo(long TotalBytes, long AvailableBytes);

/// <summary>Tempos acumulados da máquina (GetSystemTimes), em 100 ns. Kernel inclui o ocioso.</summary>
internal readonly record struct SystemCpuTimes(long Idle, long Kernel, long User);

/// <summary>Lista todos os processos da máquina num único instante.</summary>
internal interface IProcessSource
{
    /// <returns>Os processos, ou null se a leitura falhou (falha transitória: o chamador reaproveita o ciclo anterior).</returns>
    IReadOnlyList<RawProcess>? ReadAll();
}

/// <summary>Leitura direta (sem cache) dos dados de um processo.</summary>
internal interface IProcessReader
{
    ProcessDetails Read(int pid);

    string? ReadWorkingDirectory(int pid);
}

/// <summary>Detalhes por processo com cache por (pid, createTime).</summary>
internal interface IProcessDetailsProvider
{
    ProcessDetails Get(in RawProcess process);

    void Prune(IReadOnlyList<RawProcess> alive);
}

internal interface ISystemInfoSource
{
    int LogicalCores { get; }

    MemoryInfo ReadMemory();

    SystemCpuTimes ReadCpuTimes();
}
