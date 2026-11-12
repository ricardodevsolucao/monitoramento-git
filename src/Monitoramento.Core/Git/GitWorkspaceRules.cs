using Monitoramento.Core.Models;

namespace Monitoramento.Core.Git;

/// <param name="IsKnownRepo">Recebe a raiz já normalizada (<see cref="KnownGitRepos.Normalize"/>).</param>
/// <param name="ProjectIds">Ids dos projetos salvos; <c>projectId</c> fora deles vira null.</param>
/// <param name="SavedPaths">
/// Pastas e repos das abas já gravadas (forma de <see cref="FolderPath.Trim"/>): continuam aceitos mesmo que não
/// estejam conhecidos agora (ex.: pendrive ausente). Só não são fixados até voltarem a ser válidos.
/// </param>
internal sealed record WorkspaceContext(Func<string, bool> IsKnownRepo, IReadOnlySet<string> ProjectIds, IReadOnlySet<string> SavedPaths);

/// <summary>Saneamento do workspace git vindo da UI (<c>setGitWorkspace</c>).</summary>
internal static class GitWorkspaceRules
{
    public static GitWorkspace Sanitize(GitWorkspace? input, WorkspaceContext context)
    {
        if (input is null)
        {
            return new GitWorkspace();
        }

        var tabs = new List<GitTab>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in input.Tabs ?? [])
        {
            if (tabs.Count == GitWorkspace.MaxTabs)
            {
                break;
            }

            if (tab is null || Accept(tab.Folder, context) is not { } requested)
            {
                continue;
            }

            var repos = (tab.Repos ?? [])
                .Select(r => AcceptRepo(r, context))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(GitWorkspace.MaxReposPerTab)
                .ToList();
            if (repos.Count == 0)
            {
                continue; // aba sem nenhum repositório aceito sai
            }

            var folder = TabFolder(requested, repos);
            if (!folders.Add(folder))
            {
                continue;
            }

            var wantedRepo = tab.ActiveRepo is null ? null : Accept(tab.ActiveRepo, context);
            tabs.Add(new GitTab
            {
                Folder = folder,
                Name = Name(tab.Name, folder),
                Repos = repos,
                ProjectId = tab.ProjectId is { } id && context.ProjectIds.Contains(id) ? id : null,
                ActiveRepo = repos.Find(r => FolderPath.Same(r, wantedRepo)) ?? repos[0],
                WorktreeOf = tab.WorktreeOf is null ? null : Accept(tab.WorktreeOf, context),
            });
        }

        var wantedTab = input.Active is null ? null : Accept(input.Active, context);
        return new GitWorkspace
        {
            Docked = input.Docked,
            DockWidthPct = ClampDockWidth(input.DockWidthPct),
            Tabs = tabs,
            Active = tabs.Find(t => FolderPath.Same(t.Folder, wantedTab))?.Folder ?? tabs.FirstOrDefault()?.Folder,
        };
    }

    /// <summary>Mesmo conteúdo (para não regravar a config nem refazer a fixação à toa).</summary>
    public static bool SameValue(GitWorkspace a, GitWorkspace b) =>
        a.Docked == b.Docked
        && a.DockWidthPct.Equals(b.DockWidthPct)
        && string.Equals(a.Active, b.Active, StringComparison.Ordinal)
        && a.Tabs.Count == b.Tabs.Count
        && a.Tabs.Zip(b.Tabs).All(pair => SameTab(pair.First, pair.Second));

    public static double ClampDockWidth(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, GitWorkspace.MinDockWidthPct, GitWorkspace.MaxDockWidthPct)
            : GitWorkspace.DefaultDockWidthPct;

    /// <summary>Nome aparado e cortado em 80; vazio vira o nome da pasta.</summary>
    public static string Name(string? name, string folder)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            trimmed = FolderName(folder);
        }

        return trimmed.Length > GitProject.MaxNameLength ? trimmed[..GitProject.MaxNameLength].TrimEnd() : trimmed;
    }

    /// <summary>Último segmento da pasta ("C:\src\gsintegra" → "gsintegra"); a própria pasta se for raiz.</summary>
    public static string FolderName(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? folder : name;
    }

    /// <summary>Caminho local normalizado; ou, se não for local agora, o mesmo caminho já gravado numa aba.</summary>
    private static string? Accept(string? path, WorkspaceContext context)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (KnownGitRepos.Normalize(path) is { } normalized)
        {
            return normalized;
        }

        var trimmed = FolderPath.Trim(path);
        return context.SavedPaths.Contains(trimmed) ? trimmed : null;
    }

    private static string? AcceptRepo(string? path, WorkspaceContext context)
    {
        if (Accept(path, context) is not { } repo)
        {
            return null;
        }

        return context.IsKnownRepo(repo) || context.SavedPaths.Contains(repo) ? repo : null;
    }

    /// <summary>
    /// A pasta da aba precisa ser raiz ou mãe direta de um repo da aba, sem link/junção e sem ser pasta ampla; senão
    /// vira a raiz do primeiro repo.
    /// </summary>
    private static string TabFolder(string requested, List<string> repos)
    {
        var related = repos.Exists(r => FolderPath.Same(r, requested) || FolderPath.IsParentOf(requested, r));
        return related && !BroadFolders.IsBroad(requested) && !GitRepositoryLocator.IsLink(requested) ? requested : repos[0];
    }

    private static bool SameTab(GitTab a, GitTab b) =>
        string.Equals(a.Folder, b.Folder, StringComparison.Ordinal)
        && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
        && string.Equals(a.ProjectId, b.ProjectId, StringComparison.Ordinal)
        && string.Equals(a.ActiveRepo, b.ActiveRepo, StringComparison.Ordinal)
        && string.Equals(a.WorktreeOf, b.WorktreeOf, StringComparison.Ordinal)
        && a.Repos.SequenceEqual(b.Repos, StringComparer.Ordinal);
}
