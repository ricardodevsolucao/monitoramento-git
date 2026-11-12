namespace Monitoramento.Core.Models;

/// <param name="StatusNote">
/// Motivo (em português) de <paramref name="DirtyCount"/>/<paramref name="Ahead"/>/<paramref name="Behind"/> não terem sido lidos
/// por segurança (ex.: a config do repositório define filtro ou fsmonitor); null quando o status foi lido ou ainda não chegou.
/// </param>
/// <param name="MainRepoRoot">Raiz do repositório principal quando <paramref name="RepoRoot"/> é um worktree vinculado; senão null.</param>
public sealed record GitInfo(
    string RepoRoot,
    string RepoName,
    string? Branch,
    int? DirtyCount,
    int? Ahead,
    int? Behind,
    string? StatusNote = null,
    string? MainRepoRoot = null);
