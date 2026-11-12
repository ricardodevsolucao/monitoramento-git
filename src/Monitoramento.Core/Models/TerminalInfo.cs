namespace Monitoramento.Core.Models;

/// <summary>Terminal embutido (ConPTY) aberto pelo app.</summary>
/// <param name="ProjectFolder">Pasta do projeto git salvo que contém <paramref name="Folder"/>, ou null.</param>
/// <param name="SavedId">Processo salvo que roda neste terminal, ou null (shell aberto com termOpen).</param>
/// <param name="StartedAt">ISO 8601 do último início.</param>
public sealed record TerminalInfo(
    string Id,
    string Title,
    string Folder,
    string? ProjectFolder,
    string? SavedId,
    int? Pid,
    bool Running,
    int? ExitCode,
    string StartedAt,
    int Cols,
    int Rows);
