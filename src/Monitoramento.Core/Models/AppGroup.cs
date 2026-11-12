namespace Monitoramento.Core.Models;

/// <summary>Processos agrupados pela mesma aplicação (mesmo executável).</summary>
public sealed record AppGroup
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string ExeName { get; init; }
    public string? ExePath { get; init; }
    public string? IconKey { get; init; }
    public long MemoryBytes { get; init; }
    public double MemoryPercent { get; init; }
    public double CpuPercent { get; init; }
    public int ProcessCount { get; init; }
    public bool Favorite { get; init; }
    public IReadOnlyList<GitInfo> Repos { get; init; } = [];
    public IReadOnlyList<long> MemoryHistory { get; init; } = [];
    public IReadOnlyList<ProcessInfo> Processes { get; init; } = [];
}
