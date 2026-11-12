using System.Text.Json;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Bridge;

/// <summary>Mensagem UI → host: { id, type, payload }.</summary>
internal sealed record BridgeRequest(string? Id, string? Type, JsonElement Payload);

internal sealed record KeyPayload(string? Key);

internal sealed record IdPayload(string? Id);

internal sealed record PidPayload(int? Pid);

/// <param name="StartTime">ProcessInfo.startTime (ISO) que a UI viu; confere a identidade do processo.</param>
internal sealed record KillProcessPayload(int? Pid, bool Tree, string? StartTime);

internal sealed record PathPayload(string? Path);

internal sealed record KeysPayload(IReadOnlyList<string?>? Keys);

internal sealed record SavePayload(SavedProcess? Saved);

internal sealed record SettingsPayload(Settings? Settings);

internal sealed record ReadyData(Snapshot Snapshot, AppConfig Config, string? ConfigLoadError);

internal sealed record FavoritesData(IReadOnlyList<string> Favorites);

internal sealed record IconsData(IReadOnlyDictionary<string, string?> Icons);

internal sealed record SavedData(SavedProcess Saved);

/// <param name="Warning">Aviso em português quando o comando tem metacaracteres do cmd fora de aspas.</param>
internal sealed record DraftData(SavedProcess Saved, string? Warning);

internal sealed record StateData(SavedProcessState State);

internal sealed record KilledData(int Killed);

internal sealed record SettingsData(Settings Settings);

internal sealed record EmptyData;

/// <param name="MaxCommits">Padrão 150, máximo 500 (fora disso é ajustado).</param>
/// <param name="Force">Atualização manual: ignora o cache de 5 s.</param>
internal sealed record GitDetailsPayload(string? RepoRoot, int? MaxCommits, bool? Force = null);

internal sealed record GitDetailsData(GitDetails Details);

/// <summary>gitMergeAbort, gitFetch, gitPull e gitPush.</summary>
internal sealed record GitRepoPayload(string? RepoRoot);

/// <summary>gitStage, gitUnstage e gitDiscard (vazio = todos, exceto no descarte).</summary>
internal sealed record GitPathsPayload(string? RepoRoot, IReadOnlyList<string?>? Paths);

internal sealed record GitCommitPayload(string? RepoRoot, string? Message, bool All, bool Amend, bool Push);

internal sealed record GitCheckoutPayload(string? RepoRoot, string? Branch, bool Create, string? StartPoint);

internal sealed record GitMergePayload(string? RepoRoot, string? Branch);

/// <param name="Output">Saída relevante do git (até 4 KB), ou null.</param>
internal sealed record GitActionData(GitDetails Details, string? Output);

/// <summary>saveGitProject: só id, nome e pasta vêm da UI (repos são recalculados pelo Core).</summary>
internal sealed record GitProjectInput(string? Id, string? Name, string? Folder);

internal sealed record SaveGitProjectPayload(GitProjectInput? Project);

internal sealed record GitProjectDraftData(GitProject Project, IReadOnlyList<GitInfo> Repos);

internal sealed record GitProjectData(GitProject Project);

internal sealed record SetGitWorkspacePayload(GitWorkspace? Workspace);

internal sealed record GitWorkspaceData(GitWorkspace Workspace);

internal sealed record FolderPayload(string? Folder);

/// <param name="PackageManager">"npm", "pnpm", "yarn" ou null.</param>
/// <param name="Scripts">Nomes das chaves de "scripts" (não os comandos).</param>
internal sealed record RepoScripts(string RepoRoot, string? PackageManager, IReadOnlyList<string> Scripts);

internal sealed record ProjectScriptsData(IReadOnlyList<RepoScripts> Repos);

/// <param name="ConfirmUnsafe">O usuário confirmou abrir mesmo com a config git do repositório podendo executar programas.</param>
internal sealed record TermOpenPayload(string? Folder, int? Cols, int? Rows, bool? ConfirmUnsafe = null);

internal sealed record TermRunSavedInPayload(string? SavedId, string? WorktreePath, int? Cols, int? Rows, bool? ConfirmUnsafe = null);

internal sealed record GitWorktreeAddPayload(string? RepoRoot, string? Branch, string? NewBranch, string? StartPoint, string? Path, bool? CopyEnv);

internal sealed record GitWorktreeAddData(GitWorktree Worktree, GitDetails Details, IReadOnlyList<string> Copied);

internal sealed record GitWorktreeRemovePayload(string? RepoRoot, string? Path, bool Force);

internal sealed record TermInputPayload(string? Id, string? Data);

internal sealed record TermResizePayload(string? Id, int? Cols, int? Rows);

internal sealed record TerminalData(TerminalInfo Terminal);

/// <param name="Seq">Seq do último caractere de <paramref name="Data"/>: a UI descarta termOutput com seq menor ou igual.</param>
internal sealed record TermAttachData(string Data, long Seq);

/// <summary>Push <c>termOutput</c>.</summary>
internal sealed record TermOutputPayload(string Id, string Data, long Seq);

/// <summary>Push <c>termExit</c>.</summary>
internal sealed record TermExitPayload(string Id, int ExitCode);
