namespace Monitoramento.Core.Models;

public sealed record ProcessInfo
{
    public int Pid { get; init; }
    public int ParentPid { get; init; }
    public required string Name { get; init; }
    public string? ExePath { get; init; }
    public string? CommandLine { get; init; }
    public string? WorkingDirectory { get; init; }
    public long MemoryBytes { get; init; }
    public double MemoryPercent { get; init; }
    public double CpuPercent { get; init; }
    public string? StartTime { get; init; }
    public bool AccessDenied { get; init; }
    public bool IsGitProcess { get; init; }
    public GitInfo? Git { get; init; }

    /// <summary>Repositórios nas subpastas imediatas quando a pasta de trabalho não está num repositório (workspace).</summary>
    public IReadOnlyList<GitInfo> SubRepos { get; init; } = [];
    public required string FavoriteKey { get; init; }
    public bool Favorite { get; init; }
    public string? SavedId { get; init; }

    /// <summary>CreateTime do processo (FILETIME UTC). Interno: identifica o processo junto do pid; não vai para o JSON.</summary>
    internal long CreateTime { get; init; }
}
