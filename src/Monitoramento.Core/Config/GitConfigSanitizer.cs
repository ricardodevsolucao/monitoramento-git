using Monitoramento.Core.Git;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Config;

/// <summary>
/// Saneamento estrutural de <c>gitProjects</c>/<c>gitWorkspace</c> lidos do config.json (nulos, duplicados, limites,
/// letra de unidade maiúscula). Não olha o disco: uma pasta numa unidade removível ausente não apaga o projeto. Os
/// caminhos só são usados depois de validados (LocalPath + raiz de repositório de verdade) na fixação em
/// <see cref="KnownGitRepos"/>. O que for descartado por limite vira aviso em português.
/// </summary>
internal static class GitConfigSanitizer
{
    public static IReadOnlyList<GitProject> Projects(IReadOnlyList<GitProject>? projects, ICollection<string> warnings)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<GitProject>();
        var overLimit = false;
        var reposCut = false;
        foreach (var project in projects ?? [])
        {
            if (project is null || string.IsNullOrWhiteSpace(project.Folder))
            {
                continue;
            }

            // id ausente (config editada à mão): gera um novo em vez de perder o projeto
            var id = string.IsNullOrWhiteSpace(project.Id) ? Guid.NewGuid().ToString() : project.Id;
            var folder = FolderPath.Trim(project.Folder);
            if (ids.Contains(id) || folders.Contains(folder))
            {
                continue;
            }

            if (result.Count == GitProject.MaxProjects)
            {
                overLimit = true;
                break;
            }

            ids.Add(id);
            folders.Add(folder);
            result.Add(project with
            {
                Id = id,
                Folder = folder,
                Name = GitWorkspaceRules.Name(project.Name, folder),
                Repos = Paths(project.Repos, GitRepositoryLocator.MaxChildRepos, ref reposCut),
            });
        }

        if (overLimit)
        {
            warnings.Add($"Havia mais de {GitProject.MaxProjects} projetos git salvos; só os {GitProject.MaxProjects} primeiros foram mantidos.");
        }

        if (reposCut)
        {
            warnings.Add($"Algum projeto git salvo tinha mais de {GitRepositoryLocator.MaxChildRepos} repositórios; só os {GitRepositoryLocator.MaxChildRepos} primeiros foram mantidos.");
        }

        return result;
    }

    public static GitWorkspace Workspace(GitWorkspace? workspace, ICollection<string> warnings)
    {
        if (workspace is null)
        {
            return new GitWorkspace();
        }

        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tabs = new List<GitTab>();
        var reposCut = false;
        foreach (var tab in workspace.Tabs ?? [])
        {
            if (tab is null || string.IsNullOrWhiteSpace(tab.Folder) || !folders.Add(FolderPath.Trim(tab.Folder)))
            {
                continue;
            }

            if (tabs.Count == GitWorkspace.MaxTabs)
            {
                warnings.Add($"Havia mais de {GitWorkspace.MaxTabs} abas no workspace git; só as {GitWorkspace.MaxTabs} primeiras foram mantidas.");
                break;
            }

            var folder = FolderPath.Trim(tab.Folder);
            tabs.Add(tab with
            {
                Folder = folder,
                Name = GitWorkspaceRules.Name(tab.Name, folder),
                Repos = Paths(tab.Repos, GitWorkspace.MaxReposPerTab, ref reposCut),
                ActiveRepo = string.IsNullOrWhiteSpace(tab.ActiveRepo) ? null : FolderPath.Trim(tab.ActiveRepo),
                WorktreeOf = string.IsNullOrWhiteSpace(tab.WorktreeOf) ? null : FolderPath.Trim(tab.WorktreeOf),
            });
        }

        if (reposCut)
        {
            warnings.Add($"Alguma aba do workspace git tinha mais de {GitWorkspace.MaxReposPerTab} repositórios; só os {GitWorkspace.MaxReposPerTab} primeiros foram mantidos.");
        }

        return workspace with
        {
            DockWidthPct = GitWorkspaceRules.ClampDockWidth(workspace.DockWidthPct),
            Tabs = tabs,
            Active = string.IsNullOrWhiteSpace(workspace.Active) ? null : FolderPath.Trim(workspace.Active),
        };
    }

    private static List<string> Paths(IReadOnlyList<string>? paths, int max, ref bool cut)
    {
        var distinct = (paths ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(FolderPath.Trim)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count > max)
        {
            cut = true;
            distinct.RemoveRange(max, distinct.Count - max);
        }

        return distinct;
    }
}
