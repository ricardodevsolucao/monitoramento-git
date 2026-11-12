using System.Text.Json.Serialization;

namespace Monitoramento.Core.Models;

/// <summary>Visão Git de um repositório (aba Git): branch, mudanças, árvore de commits com o layout das trilhas.</summary>
/// <param name="Changes">Null quando o status não pode ser lido com segurança (motivo em <see cref="GitInfo.StatusNote"/>).</param>
/// <param name="Commits">Ordem topológica, mais novo primeiro (branches locais + remotas + tags + HEAD).</param>
/// <param name="Truncated">Há mais commits além de maxCommits.</param>
/// <param name="Error">Falha ao ler o histórico (português); o painel mostra o que tiver.</param>
public sealed record GitDetails(
    GitInfo Repo,
    GitHead Head,
    IReadOnlyList<GitBranch> Branches,
    IReadOnlyList<GitChange>? Changes,
    IReadOnlyList<GitCommit> Commits,
    GitGraph Graph,
    bool Truncated,
    string? Error,
    bool Merging,
    bool Rebasing,
    IReadOnlyList<string> Remotes,
    int StagedCount,
    int UnstagedCount,
    int UntrackedCount,
    int ConflictCount,
    IReadOnlyList<GitWorktree> Worktrees);

/// <summary>Worktree do repositório (<c>git worktree list --porcelain -z</c>), já validado.</summary>
/// <param name="Head">Sha curto (7) do HEAD do worktree.</param>
/// <param name="DirtyCount">Mudanças, só para worktree que já é repositório conhecido (cache de status); senão null.</param>
public sealed record GitWorktree(
    string Path,
    string? Branch,
    string Head,
    bool IsMain,
    bool Detached,
    bool Locked,
    string? LockReason,
    bool Prunable,
    int? DirtyCount);

public sealed record GitHead(string? Branch, bool Detached, string? Sha, string? Upstream);

/// <param name="LastCommitDate">Data do commit (committer), ISO estrito.</param>
public sealed record GitBranch(
    string Name,
    bool Current,
    bool Remote,
    string? Upstream,
    int? Ahead,
    int? Behind,
    string Sha,
    string LastCommitDate,
    string? WorktreePath = null);

/// <param name="OrigPath">Caminho anterior em renomeação/cópia.</param>
public sealed record GitChange(string Path, string? OrigPath, GitIndexState? Index, GitWorktreeState? Worktree, bool Conflict);

/// <param name="Date">Data do autor, ISO estrito.</param>
/// <param name="Lane">Trilha do nó (0 = mais à esquerda).</param>
/// <param name="Edges">Linhas deste nó até a próxima linha.</param>
/// <param name="PassThrough">Trilhas que só atravessam esta linha.</param>
public sealed record GitCommit(
    string Sha,
    string ShortSha,
    IReadOnlyList<string> Parents,
    string Subject,
    string Author,
    string Date,
    IReadOnlyList<GitRef> Refs,
    int Lane,
    IReadOnlyList<GitEdge> Edges,
    IReadOnlyList<int> PassThrough);

/// <summary>Decoração de um commit. "head" = branch atual (ou "HEAD" quando destacado).</summary>
public sealed record GitRef(string Name, GitRefKind Kind);

public sealed record GitEdge(int FromLane, int ToLane, string ToSha, GitEdgeKind Kind);

/// <param name="Lanes">Número máximo de trilhas, para a UI dimensionar a coluna do gráfico.</param>
public sealed record GitGraph(int Lanes);

/// <summary>Estado na área de preparação; serializa como a letra do git.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GitIndexState>))]
public enum GitIndexState
{
    M,
    A,
    D,
    R,
    C,
    U,
    T,
}

/// <summary>Estado na árvore de trabalho; <see cref="Untracked"/> serializa como "?".</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GitWorktreeState>))]
public enum GitWorktreeState
{
    M,
    D,
    U,
    T,
    [JsonStringEnumMemberName("?")]
    Untracked,
}

[JsonConverter(typeof(JsonStringEnumConverter<GitRefKind>))]
public enum GitRefKind
{
    [JsonStringEnumMemberName("head")]
    Head,
    [JsonStringEnumMemberName("branch")]
    Branch,
    [JsonStringEnumMemberName("remote")]
    Remote,
    [JsonStringEnumMemberName("tag")]
    Tag,
}

[JsonConverter(typeof(JsonStringEnumConverter<GitEdgeKind>))]
public enum GitEdgeKind
{
    [JsonStringEnumMemberName("parent")]
    Parent,
    [JsonStringEnumMemberName("merge")]
    Merge,
}
