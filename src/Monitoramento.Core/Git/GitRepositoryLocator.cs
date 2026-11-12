using Monitoramento.Core.Security;

namespace Monitoramento.Core.Git;

/// <summary>Raiz do repositório (pasta de trabalho) e a pasta git real (que pode estar em outro lugar, no caso de worktree).</summary>
internal sealed record GitRepoLocation(string RepoRoot, string GitDir);

/// <summary>Detecção de repositório e branch sem executar o git. Só segue caminhos locais (<see cref="LocalPath"/>).</summary>
internal static class GitRepositoryLocator
{
    private const string GitDirPrefix = "gitdir:";
    private const string RefPrefix = "ref:";
    private const string HeadsPrefix = "refs/heads/";
    private const int ShortShaLength = 7;

    /// <summary>Máximo de repositórios devolvidos por <see cref="FindChildRepos"/>.</summary>
    public const int MaxChildRepos = 12;

    /// <summary>Limite de subpastas olhadas (pasta de trabalho como C:\Windows tem centenas).</summary>
    private const int MaxScannedFolders = 256;

    private static readonly HashSet<string> IgnoredChildNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "dist",
    };

    /// <summary>Sobe a partir de <paramref name="startDirectory"/> procurando ".git" (pasta ou arquivo "gitdir:").</summary>
    public static GitRepoLocation? Locate(string? startDirectory)
    {
        if (!LocalPath.TryGetLocal(startDirectory, out var start))
        {
            return null;
        }

        try
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, ".git");
                if (!TryGetAttributes(candidate, out var attributes))
                {
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return null; // ".git" como link/junção: não segue (poderia apontar para um caminho UNC e disparar SMB)
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    return new GitRepoLocation(TrimEnd(dir.FullName), candidate);
                }

                var gitDir = ReadGitDirFile(candidate, dir.FullName); // arquivo "gitdir:" (worktree/submódulo)
                return gitDir is null ? null : new GitRepoLocation(TrimEnd(dir.FullName), gitDir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Repositórios nas subpastas imediatas de uma pasta que não é repositório (workspace com vários repos, como o
    /// VS Code faz). 1 nível, até <see cref="MaxChildRepos"/>, em ordem de nome; ignora ocultas, links/junções,
    /// pastas de dependência/saída e worktrees vinculados (eles aparecem só como worktree do repo principal). Só caminho
    /// local.
    /// </summary>
    public static IReadOnlyList<GitRepoLocation> FindChildRepos(string? directory)
    {
        if (!LocalPath.TryGetLocal(directory, out var parent))
        {
            return [];
        }

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            };
            var children = Directory.EnumerateDirectories(parent, "*", options)
                .Take(MaxScannedFolders)
                .Where(child => !IgnoredChild(Path.GetFileName(child)))
                .Order(StringComparer.OrdinalIgnoreCase);

            var found = new List<GitRepoLocation>();
            foreach (var child in children)
            {
                if (!TryGetAttributes(Path.Combine(child, ".git"), out _))
                {
                    continue;
                }

                var location = Locate(child);
                if (location is not null && string.Equals(location.RepoRoot, TrimEnd(child), StringComparison.OrdinalIgnoreCase)
                    && !GitWorktrees.IsLinkedWorktree(location))
                {
                    found.Add(location);
                    if (found.Count == MaxChildRepos)
                    {
                        break;
                    }
                }
            }

            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    private static bool IgnoredChild(string name) =>
        name.Length == 0 || name[0] == '.' || IgnoredChildNames.Contains(name);

    /// <summary>
    /// Pasta comum do repositório (worktree: arquivo "commondir"); a própria <paramref name="gitDir"/> se não houver.
    /// Null se "commondir" apontar para fora de uma unidade local.
    /// </summary>
    public static string? CommonDir(string gitDir)
    {
        var file = Path.Combine(gitDir, "commondir");
        if (!TryGetAttributes(file, out var attributes))
        {
            return gitDir;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return null;
        }

        var target = File.ReadLines(file).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(target))
        {
            return gitDir;
        }

        var common = ResolveLocal(target, gitDir);
        return common is null || IsLink(common) ? null : common;
    }

    /// <summary>Branch atual a partir de HEAD; HEAD destacado devolve o SHA curto (7).</summary>
    public static string? ReadBranch(string gitDir)
    {
        try
        {
            var headFile = Path.Combine(gitDir, "HEAD");
            if (IsLink(headFile))
            {
                return null;
            }

            var head = File.ReadAllText(headFile).Trim();
            if (head.StartsWith(RefPrefix, StringComparison.Ordinal))
            {
                var reference = head[RefPrefix.Length..].Trim();
                return reference.StartsWith(HeadsPrefix, StringComparison.Ordinal) ? reference[HeadsPrefix.Length..] : reference;
            }

            return head.Length >= ShortShaLength ? head[..ShortShaLength] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? ReadGitDirFile(string file, string baseDirectory)
    {
        var line = File.ReadLines(file).FirstOrDefault()?.Trim();
        if (line is null || !line.StartsWith(GitDirPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var full = ResolveLocal(line[GitDirPrefix.Length..].Trim(), baseDirectory);
        return full is not null && TryGetAttributes(full, out var attributes)
            && attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint)
            ? full
            : null;
    }

    /// <summary>Verdadeiro se o caminho existe e é link simbólico/junção (atributos lidos sem seguir o link).</summary>
    public static bool IsLink(string path) =>
        TryGetAttributes(path, out var attributes) && attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>Atributos do próprio item (não do alvo de um link); false se não existir ou não puder ser lido.</summary>
    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            attributes = default;
            return false;
        }
    }

    /// <summary>Caminho de arquivo do git (absoluto ou relativo à <paramref name="baseDirectory"/>), só se for local.</summary>
    private static string? ResolveLocal(string target, string baseDirectory)
    {
        if (target.Length == 0)
        {
            return null;
        }

        // começa por separador ("\\host", "\\?\", "\x") ou é absoluto: usa como está e a validação local recusa o que não for
        var candidate = target[0] is '\\' or '/' || Path.IsPathRooted(target) ? target : Path.Combine(baseDirectory, target);
        return LocalPath.TryGetLocal(candidate, out var full) ? full : null;
    }

    private static string TrimEnd(string path) =>
        path.Length > 3 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
}
