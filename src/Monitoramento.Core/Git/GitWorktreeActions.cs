using System.Text;
using Monitoramento.Core.Interop;

namespace Monitoramento.Core.Git;

/// <summary>Regras de caminho e de cópia de .env para <c>gitWorktreeAdd</c> (sem rodar git).</summary>
internal static class GitWorktreePaths
{
    public const string PathPrefix = "Caminho do worktree inválido: ";
    public const long MaxEnvFileBytes = 1024 * 1024;
    private const int MaxSlugLength = 50;

    /// <summary>Nome de pasta a partir da branch: [a-z0-9._-], "/" vira "-", sem repetição de "-".</summary>
    public static string Slug(string branch)
    {
        var builder = new StringBuilder(branch.Length);
        foreach (var c in branch.ToLowerInvariant())
        {
            var mapped = c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-' ? c : '-';
            if (mapped == '-' && builder.Length > 0 && builder[^1] == '-')
            {
                continue;
            }

            builder.Append(mapped);
        }

        var slug = builder.ToString().Trim('-', '.');
        if (slug.Length > MaxSlugLength)
        {
            slug = slug[..MaxSlugLength].Trim('-', '.');
        }

        return slug.Length == 0 ? "worktree" : slug;
    }

    /// <summary>
    /// <c>&lt;pasta do projeto salvo que contém o repo, senão pasta-mãe do repo&gt;.worktrees\&lt;repo&gt;-&lt;slug&gt;</c>,
    /// com sufixo -2, -3… se já existir. Valida com as mesmas regras do caminho informado (a pasta ".worktrees" pode
    /// ainda não existir).
    /// </summary>
    public static (string? Path, string? Error) Default(string mainRoot, string branch, Func<string, string?> projectFolderOf)
    {
        var container = projectFolderOf(mainRoot) ?? Path.GetDirectoryName(mainRoot);
        if (container is null || container.Length <= 3)
        {
            return (null, PathPrefix + "o repositório está na raiz da unidade; informe a pasta do worktree.");
        }

        var baseDir = FolderPath.Trim(container) + ".worktrees";
        var name = Path.GetFileName(mainRoot) + "-" + Slug(branch);
        var candidate = Path.Combine(baseDir, name);
        for (var i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++)
        {
            candidate = Path.Combine(baseDir, $"{name}-{i}");
        }

        return Validate(candidate, parentMayBeCreated: true);
    }

    /// <summary>
    /// Caminho de worktree aceitável: LocalPath, não amplo, pasta-mãe existente (salvo a ".worktrees" padrão), destino
    /// inexistente ou vazio, sem link/junção no caminho e fora da working tree de qualquer repositório.
    /// </summary>
    public static (string? Path, string? Error) Validate(string? path, bool parentMayBeCreated = false)
    {
        if (KnownGitRepos.Normalize(path) is not { } full)
        {
            return (null, PathPrefix + "use uma pasta local (não de rede).");
        }

        if (BroadFolders.IsBroad(full) || Path.GetDirectoryName(full) is not { } parent)
        {
            return (null, PathPrefix + "pasta ampla demais (raiz da unidade, pasta do usuário ou do sistema).");
        }

        var grandParent = Path.GetDirectoryName(parent);
        if (!Directory.Exists(parent) && !(parentMayBeCreated && grandParent is not null && Directory.Exists(grandParent)))
        {
            return (null, PathPrefix + $"a pasta-mãe \"{parent}\" não existe.");
        }

        if (PassesThroughLink(full))
        {
            return (null, PathPrefix + "o caminho passa por um link (junção ou link simbólico).");
        }

        if (File.Exists(full) || (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any()))
        {
            return (null, PathPrefix + $"a pasta \"{full}\" já existe e não está vazia.");
        }

        var existing = Directory.Exists(parent) ? parent : grandParent!;
        if (GitRepositoryLocator.Locate(existing) is { } repo)
        {
            return (null, PathPrefix + $"fica dentro de outro repositório git (\"{repo.RepoRoot}\").");
        }

        return (full, null);
    }

    /// <summary>
    /// Arquivos <c>.env</c> e <c>.env.*</c> da raiz do repositório principal que não estão versionados (exceto
    /// .env.example e .env.sample), até 1 MB, sem link; copiados para a raiz do worktree sem sobrescrever.
    /// </summary>
    public static IReadOnlyList<string> CopyEnvFiles(string mainRoot, string worktreeRoot, IReadOnlySet<string> tracked)
    {
        var copied = new List<string>();
        foreach (var name in EnvCandidates(mainRoot))
        {
            if (tracked.Contains(name))
            {
                continue;
            }

            var target = Path.Combine(worktreeRoot, name);
            if (File.Exists(target) || Directory.Exists(target) || GitRepositoryLocator.IsLink(target))
            {
                continue;
            }

            if (FileSystemNative.OpenRegularFileNoFollow(Path.Combine(mainRoot, name)) is not { } opened)
            {
                continue;
            }

            try
            {
                using var source = new FileStream(opened.Handle, FileAccess.Read);
                if (opened.Size > MaxEnvFileBytes)
                {
                    continue;
                }

                var bytes = new byte[opened.Size];
                var read = source.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                destination.Write(bytes, 0, read);
                copied.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // já existe (corrida) ou sem permissão: pula
            }
        }

        return copied;
    }

    /// <summary>Nomes candidatos na raiz do repositório principal (só o nome do arquivo).</summary>
    public static IReadOnlyList<string> EnvCandidates(string mainRoot)
    {
        try
        {
            return Directory.EnumerateFiles(mainRoot, ".env*", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint })
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => n.Equals(".env", StringComparison.OrdinalIgnoreCase)
                    || (n.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
                        && !n.Equals(".env.example", StringComparison.OrdinalIgnoreCase)
                        && !n.Equals(".env.sample", StringComparison.OrdinalIgnoreCase)))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Algum segmento existente do caminho (a partir da unidade) é junção/link simbólico.</summary>
    private static bool PassesThroughLink(string full)
    {
        var current = Path.GetPathRoot(full)!;
        foreach (var segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (GitRepositoryLocator.IsLink(current))
            {
                return true;
            }
        }

        return false;
    }
}
