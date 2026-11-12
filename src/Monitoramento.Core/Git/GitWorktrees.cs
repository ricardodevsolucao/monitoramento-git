namespace Monitoramento.Core.Git;

/// <summary>Uma entrada de <c>git worktree list --porcelain -z</c>, ainda não validada.</summary>
internal sealed record RawWorktree(string Path, string? HeadSha, string? Branch, bool Detached, bool Bare, bool Locked, string? LockReason, bool Prunable);

/// <summary>Parser da saída de <c>git worktree list --porcelain -z</c> (campos terminados em NUL; registro termina num campo vazio).</summary>
internal static class GitWorktreeParser
{
    private const string HeadsPrefix = "refs/heads/";

    public static IReadOnlyList<RawWorktree> Parse(string output)
    {
        var list = new List<RawWorktree>();
        RawWorktree? current = null;
        foreach (var field in output.Split('\0'))
        {
            if (field.Length == 0)
            {
                Flush();
                continue;
            }

            var space = field.IndexOf(' ', StringComparison.Ordinal);
            var key = space < 0 ? field : field[..space];
            var value = space < 0 ? null : field[(space + 1)..];
            if (key == "worktree")
            {
                Flush();
                current = new RawWorktree(value ?? string.Empty, null, null, false, false, false, null, false);
                continue;
            }

            if (current is null)
            {
                continue; // lixo antes de um "worktree"
            }

            current = key switch
            {
                "HEAD" => current with { HeadSha = value },
                "branch" => current with { Branch = value is not null && value.StartsWith(HeadsPrefix, StringComparison.Ordinal) ? value[HeadsPrefix.Length..] : value },
                "detached" => current with { Detached = true },
                "bare" => current with { Bare = true },
                "locked" => current with { Locked = true, LockReason = string.IsNullOrEmpty(value) ? null : value },
                "prunable" => current with { Prunable = true },
                _ => current,
            };
        }

        Flush();
        return list;

        void Flush()
        {
            if (current is { Path.Length: > 0 })
            {
                list.Add(current);
            }

            current = null;
        }
    }
}

/// <summary>
/// Worktrees vinculados: identificação e validação. O caminho de um worktree vem de dado do repositório
/// (<c>.git/worktrees/*/gitdir</c>), não confiável: só vale se for local (LocalPath), não for link/junção e o arquivo
/// <c>.git</c> dele apontar (<c>gitdir:</c>) de volta para a pasta <c>worktrees</c> da mesma <c>commondir</c>.
/// </summary>
internal static class GitWorktrees
{
    /// <summary><see cref="GitRepositoryLocator.CommonDir"/> sem exceção (arquivo ilegível → null).</summary>
    private static string? SafeCommonDir(string gitDir)
    {
        try
        {
            return GitRepositoryLocator.CommonDir(gitDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A pasta git é de um worktree vinculado (tem <c>commondir</c> apontando para outra pasta)?</summary>
    public static bool IsLinkedWorktree(GitRepoLocation location) =>
        SafeCommonDir(location.GitDir) is { } common && !FolderPath.Same(common, location.GitDir);

    /// <summary>Raiz do repositório principal de um worktree vinculado; null se não for vinculado ou não validar.</summary>
    public static string? MainRepoRoot(GitRepoLocation location) => MainRepoRootFromGitDir(location.GitDir);

    /// <inheritdoc cref="MainRepoRoot"/>
    public static string? MainRepoRootFromGitDir(string gitDir)
    {
        if (SafeCommonDir(gitDir) is not { } common || FolderPath.Same(common, gitDir))
        {
            return null;
        }

        if (!string.Equals(Path.GetFileName(FolderPath.Trim(common)), ".git", StringComparison.OrdinalIgnoreCase)
            || KnownGitRepos.Normalize(Path.GetDirectoryName(FolderPath.Trim(common))) is not { } main)
        {
            return null; // repositório "bare" ou commondir fora do padrão
        }

        return GitRepositoryLocator.Locate(main) is { } located && FolderPath.Same(located.RepoRoot, main) && FolderPath.Same(located.GitDir, common)
            ? main
            : null;
    }

    /// <summary>Pasta git comum do repositório em <paramref name="repoRoot"/> (a própria pasta .git do principal).</summary>
    public static string? CommonDirOf(string repoRoot) =>
        GitRepositoryLocator.Locate(repoRoot) is { } location ? SafeCommonDir(location.GitDir) : null;

    /// <summary>
    /// Worktree vinculado válido do repositório cuja pasta git comum é <paramref name="commonDir"/>: local, sem link,
    /// raiz de repositório com arquivo <c>.git</c> (não pasta) apontando para <c>&lt;commonDir&gt;\worktrees\&lt;nome&gt;</c>.
    /// </summary>
    public static bool IsValidLinkedWorktree(string? path, string commonDir)
    {
        if (KnownGitRepos.Normalize(path) is not { } root || KnownGitRepos.Normalize(commonDir) is not { } common
            || GitRepositoryLocator.IsLink(root))
        {
            return false;
        }

        var dotGit = Path.Combine(root, ".git");
        try
        {
            var attributes = File.GetAttributes(dotGit);
            if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        return GitRepositoryLocator.Locate(root) is { } location
            && FolderPath.Same(location.RepoRoot, root)
            && FolderPath.Same(Path.GetDirectoryName(FolderPath.Trim(location.GitDir)), Path.Combine(common, "worktrees"))
            && SafeCommonDir(location.GitDir) is { } itsCommon
            && FolderPath.Same(itsCommon, common);
    }
}
