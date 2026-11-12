using System.Globalization;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Git;

/// <summary>
/// Monta <see cref="GitDetails"/> de um repositório já validado: status (se <see cref="GitRepoSafety.CheckAsync"/> aprovar),
/// branches, remotos e log (se <see cref="GitRepoSafety.CheckLogAsync"/> também aprovar), tudo em modo leitura blindado.
/// </summary>
internal static class GitDetailsReader
{
    private const int ShortShaLength = 7;
    private const string RefPrefix = "ref:";
    private const string HeadsPrefix = "refs/heads/";

    public const string LogFailedError = "Não foi possível ler o histórico do repositório.";
    public const string LogTimeoutError = "O histórico demorou demais para ser lido; tente de novo.";
    public const string BranchesFailedError = "Não foi possível ler as branches do repositório.";
    public const string StatusFailedNote = "Status não lido: o git status falhou ou demorou demais.";

    public static GitRunRequest LogRequest(string repoRoot, int maxCommits, bool includeHead = true)
    {
        List<string> args =
        [
            "log", "-z", "--topo-order", "--date=iso-strict", "--decorate=full", "--no-color",
            "--no-show-signature", "--no-textconv", "--no-ext-diff",
            "--format=" + GitLogParser.Format,
            "-n", (maxCommits + 1).ToString(CultureInfo.InvariantCulture),
            "--branches", "--remotes", "--tags",
        ];
        if (includeHead)
        {
            args.Add("HEAD");
        }

        args.Add("--"); // "HEAD" é revisão, nunca um arquivo chamado HEAD
        return new GitRunRequest(repoRoot, args, GitRunMode.Read);
    }

    private static GitRunRequest StatusRequest(string repoRoot) => new(
        repoRoot,
        ["status", "--porcelain=v2", "--branch", "-z", "--ignore-submodules=all", "--untracked-files=normal"],
        GitRunMode.Read);

    private static GitRunRequest BranchesRequest(string repoRoot) => new(
        repoRoot,
        ["for-each-ref", "--format=" + GitRefParser.Format, "refs/heads", "refs/remotes"],
        GitRunMode.Read);

    private static GitRunRequest RemotesRequest(string repoRoot) => new(repoRoot, ["remote"], GitRunMode.Read);

    private static GitRunRequest WorktreesRequest(string repoRoot) => new(repoRoot, ["worktree", "list", "--porcelain", "-z"], GitRunMode.Read);

    /// <param name="dirtyOf">Mudanças de um worktree já conhecido (cache de status), ou null; nunca roda git a mais.</param>
    /// <returns>Null se <paramref name="repoRoot"/> não for (mais) a raiz de um repositório git.</returns>
    public static async Task<GitDetails?> ReadAsync(
        IGitRunner runner,
        string repoRoot,
        int maxCommits,
        CancellationToken cancellationToken = default,
        Func<string, int?>? dirtyOf = null)
    {
        var location = GitRepositoryLocator.Locate(repoRoot);
        if (location is null || !string.Equals(location.RepoRoot, repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var head = ReadHeadFile(location.GitDir);
        var merging = File.Exists(Path.Combine(location.GitDir, "MERGE_HEAD"));
        var rebasing = Directory.Exists(Path.Combine(location.GitDir, "rebase-merge"))
            || Directory.Exists(Path.Combine(location.GitDir, "rebase-apply"));

        if (await GitRepoSafety.CheckLogAsync(location).ConfigureAwait(false) is { } blocked)
        {
            // nada roda: nem status, nem log
            var (note, error) = BlockedMessages(blocked);
            return Build(location, head, [], null, [], [], false, error, note, merging, rebasing, []);
        }

        var statusNote = await GitRepoSafety.CheckAsync(location).ConfigureAwait(false);
        var statusTask = statusNote is null ? runner.RunAsync(StatusRequest(repoRoot), cancellationToken) : null;
        var branchesTask = runner.RunAsync(BranchesRequest(repoRoot), cancellationToken);
        var remotesTask = runner.RunAsync(RemotesRequest(repoRoot), cancellationToken);
        var logTask = runner.RunAsync(LogRequest(repoRoot, maxCommits), cancellationToken);
        var worktreesTask = runner.RunAsync(WorktreesRequest(repoRoot), cancellationToken);

        var branchesResult = await branchesTask.ConfigureAwait(false);
        var branches = branchesResult.Ok ? GitRefParser.Parse(branchesResult.Output) : [];
        var errors = new List<string>();
        if (!branchesResult.Ok)
        {
            errors.Add(BranchesFailedError);
        }

        var logResult = await logTask.ConfigureAwait(false);
        var unborn = head.Sha is null && !branches.Any(b => !b.Remote && b.Name == head.Branch);
        if (!logResult.Ok && unborn && !logResult.TimedOut)
        {
            // HEAD sem commits (repositório novo ou branch órfã): lê o resto do histórico sem o HEAD
            logResult = branches.Count == 0 ? null : await runner.RunAsync(LogRequest(repoRoot, maxCommits, includeHead: false), cancellationToken).ConfigureAwait(false);
        }

        var (entries, truncated, logError) = ReadLog(logResult, maxCommits);
        if (logError is not null)
        {
            errors.Insert(0, logError);
        }

        GitStatusDetail? status = null;
        if (statusTask is not null)
        {
            var statusResult = await statusTask.ConfigureAwait(false);
            if (statusResult.Ok)
            {
                status = PorcelainV2Parser.ParseDetailed(statusResult.Output);
            }
            else
            {
                statusNote = StatusFailedNote;
            }
        }

        var remotesResult = await remotesTask.ConfigureAwait(false);
        var remotes = remotesResult.Ok
            ? remotesResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        var worktreesResult = await worktreesTask.ConfigureAwait(false);
        var worktrees = worktreesResult.Ok ? ValidWorktrees(location, GitWorktreeParser.Parse(worktreesResult.Output), dirtyOf) : [];
        branches = branches
            .Select(b => b.Remote ? b : b with { WorktreePath = worktrees.FirstOrDefault(w => !w.Prunable && w.Branch == b.Name && !FolderPath.Same(w.Path, location.RepoRoot))?.Path })
            .ToList();

        var current = head.Branch is null ? null : branches.FirstOrDefault(b => !b.Remote && b.Name == head.Branch);
        var resolvedHead = head with { Sha = head.Sha ?? current?.Sha, Upstream = current?.Upstream };
        return Build(
            location,
            resolvedHead,
            branches,
            status,
            entries,
            remotes,
            truncated,
            errors.Count == 0 ? null : string.Join(' ', errors),
            statusNote,
            merging,
            rebasing,
            worktrees);
    }

    /// <summary>
    /// Worktrees listados pelo git, filtrados: o principal precisa ser a raiz do repositório principal; os vinculados
    /// precisam validar (<see cref="GitWorktrees.IsValidLinkedWorktree"/>). Prunable (pasta sumida) aparece para poder
    /// ser limpo, mas o caminho só passa por LocalPath e nunca é registrado.
    /// </summary>
    private static List<GitWorktree> ValidWorktrees(GitRepoLocation location, IReadOnlyList<RawWorktree> raw, Func<string, int?>? dirtyOf)
    {
        var commonDir = GitRepositoryLocator.CommonDir(location.GitDir) ?? location.GitDir;
        var mainRoot = GitWorktrees.MainRepoRoot(location) ?? location.RepoRoot;
        var list = new List<GitWorktree>();
        for (var i = 0; i < raw.Count; i++)
        {
            var entry = raw[i];
            if (entry.Bare || KnownGitRepos.Normalize(entry.Path) is not { } path || list.Any(w => FolderPath.Same(w.Path, path)))
            {
                continue;
            }

            var isMain = i == 0;
            var valid = isMain
                ? FolderPath.Same(path, mainRoot)
                : entry.Prunable || GitWorktrees.IsValidLinkedWorktree(path, commonDir);
            if (!valid)
            {
                continue;
            }

            var sha = entry.HeadSha ?? string.Empty;
            list.Add(new GitWorktree(
                path,
                entry.Branch,
                sha.Length >= ShortShaLength ? sha[..ShortShaLength] : sha,
                isMain,
                entry.Detached,
                entry.Locked,
                entry.LockReason,
                !isMain && entry.Prunable,
                entry.Prunable ? null : dirtyOf?.Invoke(path)));
        }

        return list;
    }

    private static GitDetails Build(
        GitRepoLocation location,
        GitHead head,
        IReadOnlyList<GitBranch> branches,
        GitStatusDetail? status,
        IReadOnlyList<GitLogEntry> entries,
        IReadOnlyList<string> remotes,
        bool truncated,
        string? error,
        string? note,
        bool merging,
        bool rebasing,
        IReadOnlyList<GitWorktree> worktrees)
    {
        var repoRoot = location.RepoRoot;
        var changes = status?.Changes;
        var name = Path.GetFileName(repoRoot);
        var repo = new GitInfo(
            repoRoot,
            string.IsNullOrEmpty(name) ? repoRoot : name,
            head.Branch ?? (head.Sha is { Length: >= ShortShaLength } sha ? sha[..ShortShaLength] : null),
            changes?.Count,
            status?.Ahead,
            status?.Behind,
            note,
            GitWorktrees.MainRepoRoot(location));

        var layout = GitGraphLayout.Compute(entries.Select(e => new GraphNode(e.Sha, e.Parents)).ToList());
        var commits = new List<GitCommit>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var row = layout.Rows[i];
            commits.Add(new GitCommit(
                e.Sha,
                e.Sha.Length >= ShortShaLength ? e.Sha[..ShortShaLength] : e.Sha,
                e.Parents,
                e.Subject,
                e.Author,
                e.Date,
                e.Refs,
                row.Lane,
                row.Edges,
                row.PassThrough));
        }

        var list = changes ?? [];
        return new GitDetails(
            repo,
            head,
            branches,
            changes,
            commits,
            new GitGraph(layout.Lanes),
            truncated,
            error,
            merging,
            rebasing,
            remotes,
            StagedCount: list.Count(c => !c.Conflict && c.Index is not null),
            UnstagedCount: list.Count(c => !c.Conflict && c.Worktree is GitWorktreeState.M or GitWorktreeState.D or GitWorktreeState.T),
            UntrackedCount: list.Count(c => c.Worktree == GitWorktreeState.Untracked),
            ConflictCount: list.Count(c => c.Conflict),
            Worktrees: worktrees);
    }

    private static (IReadOnlyList<GitLogEntry> Entries, bool Truncated, string? Error) ReadLog(GitRunResult? result, int maxCommits)
    {
        if (result is null)
        {
            return ([], false, null); // repositório sem nenhum commit
        }

        if (result.TimedOut)
        {
            return ([], false, LogTimeoutError);
        }

        if (result.Overflow)
        {
            // saída passou do limite: usa os commits completos e descarta o último (pode estar cortado)
            var partial = GitLogParser.Parse(result.Output);
            return (partial.Take(Math.Min(maxCommits, Math.Max(0, partial.Count - 1))).ToList(), true, null);
        }

        if (!result.Ok)
        {
            return ([], false, LogFailedError);
        }

        var entries = GitLogParser.Parse(result.Output);
        return entries.Count > maxCommits ? (entries.Take(maxCommits).ToList(), true, null) : (entries, false, null);
    }

    /// <summary>HEAD lido do arquivo (sem git): branch, ou sha quando destacado.</summary>
    private static GitHead ReadHeadFile(string gitDir)
    {
        try
        {
            var headFile = Path.Combine(gitDir, "HEAD");
            if (GitRepositoryLocator.IsLink(headFile))
            {
                return new GitHead(null, Detached: false, Sha: null, Upstream: null);
            }

            var text = File.ReadAllText(headFile).Trim();
            if (text.StartsWith(RefPrefix, StringComparison.Ordinal))
            {
                var reference = text[RefPrefix.Length..].Trim();
                var branch = reference.StartsWith(HeadsPrefix, StringComparison.Ordinal) ? reference[HeadsPrefix.Length..] : reference;
                return new GitHead(branch, Detached: false, Sha: null, Upstream: null);
            }

            return new GitHead(null, Detached: true, Sha: text.Length > 0 ? text : null, Upstream: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new GitHead(null, Detached: false, Sha: null, Upstream: null);
        }
    }

    /// <summary>Motivo da checagem estendida → (statusNote, error do histórico), ambos em português.</summary>
    private static (string Note, string Error) BlockedMessages(string reason)
    {
        if (reason.StartsWith(GitRepoSafety.StatusBlockedPrefix, StringComparison.Ordinal))
        {
            return (reason, GitRepoSafety.LogBlockedPrefix + reason[GitRepoSafety.StatusBlockedPrefix.Length..]);
        }

        return (GitRepoSafety.StatusBlockedPrefix + reason, GitRepoSafety.LogBlockedPrefix + reason);
    }
}
