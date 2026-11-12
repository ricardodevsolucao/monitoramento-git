using Monitoramento.Core.Actions;
using Monitoramento.Core.Bridge;
using Monitoramento.Core.Config;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Git;

/// <summary>
/// Projetos git salvos, workspace git e scripts de projeto. A UI não escolhe caminhos livremente: toda pasta passa por
/// <see cref="Security.LocalPath"/> (via <see cref="KnownGitRepos.Normalize"/>), pela recusa de pastas amplas
/// (<see cref="BroadFolders"/>) e pela regra de aceitação (<see cref="IsAccepted"/>); repositórios são sempre
/// recalculados pelo Core a partir da pasta. Os repositórios dos projetos e das abas ficam fixados em
/// <see cref="KnownGitRepos"/> (não expiram), só depois de validados como raiz de repositório de verdade, local e sem
/// link/junção. Nenhum git roda aqui para repositório que ainda não é conhecido.
/// </summary>
internal sealed class GitProjectService
{
    public const string UnknownFolderError = "Pasta não reconhecida: abra-a a partir de um processo ou repositório já detectado.";
    public const string BroadFolderError =
        "Pasta ampla demais para virar projeto (raiz da unidade, pasta do usuário ou do sistema). Escolha a pasta do projeto ou de um repositório.";
    public const string NoReposError = "Nenhum repositório git nessa pasta.";
    public const string NameTooLongError = "O nome do projeto pode ter no máximo 80 caracteres.";
    public const string MissingDataError = "Dados do projeto ausentes.";

    /// <summary>A pasta-mãe só é sugerida como projeto se tiver de 2 a 4 repositórios filhos (mais que isso é coleção).</summary>
    private const int MinReposForParentSuggestion = 2;
    private const int MaxReposForParentSuggestion = 4;

    /// <summary>Intervalo mínimo entre duas varreduras das pastas dos projetos salvos.</summary>
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(30);

    private readonly ConfigService _config;
    private readonly KnownGitRepos _known;
    private readonly IGitInfoResolver _git;
    private readonly Func<IReadOnlyList<ProcessInfo>> _processes;
    private readonly TimeProvider _time;
    private readonly Lock _pinGate = new();
    private readonly Lock _rescanGate = new();
    private long _pinnedGeneration = -1;
    private long? _lastRescanTimestamp;

    public GitProjectService(
        ConfigService config,
        KnownGitRepos known,
        IGitInfoResolver git,
        Func<IReadOnlyList<ProcessInfo>> processes,
        TimeProvider? time = null)
    {
        _config = config;
        _known = known;
        _git = git;
        _processes = processes;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// <c>gitProjectDraft</c>: rascunho com a pasta sugerida e os repositórios achados. Não grava. Pasta que já é
    /// projeto salvo devolve o id e o nome dele.
    /// </summary>
    public OperationResult<GitProjectDraftData> Draft(string? path)
    {
        if (Accept(path, out var folder) is { } error)
        {
            return OperationResult<GitProjectDraftData>.Failure(error);
        }

        var suggested = folder;
        IReadOnlyList<string> repos;
        if (IsRepoRoot(folder))
        {
            // raiz de repo cuja mãe tem 2 a 4 repos (ex.: gsintegra\app → gsintegra): sugere a mãe, se não for ampla e
            // também for aceita; com 5+ repos a mãe é uma coleção (ex.: Documents\Projetos) e fica o próprio repo
            var parent = KnownGitRepos.Normalize(Path.GetDirectoryName(folder));
            var siblings = parent is null || BroadFolders.IsBroad(parent) ? [] : ChildRepos(parent);
            if (parent is not null
                && siblings.Count is >= MinReposForParentSuggestion and <= MaxReposForParentSuggestion
                && IsAccepted(parent))
            {
                suggested = parent;
                repos = siblings;
            }
            else
            {
                repos = [folder];
            }
        }
        else
        {
            repos = ChildRepos(folder);
        }

        if (repos.Count == 0)
        {
            return OperationResult<GitProjectDraftData>.Failure(NoReposError);
        }

        var saved = _config.Current.GitProjects.FirstOrDefault(p => FolderPath.Same(p.Folder, suggested));
        var project = new GitProject
        {
            Id = saved?.Id ?? string.Empty,
            Name = saved?.Name ?? GitWorkspaceRules.FolderName(suggested),
            Folder = suggested,
            Repos = repos,
        };
        return OperationResult<GitProjectDraftData>.Success(new GitProjectDraftData(project, repos.Select(ToInfo).ToList()));
    }

    /// <summary><c>saveGitProject</c>: id vazio = novo; com id = renomear/atualizar. Repos vindos da UI são ignorados.</summary>
    public OperationResult<GitProject> Save(GitProjectInput? input)
    {
        if (input is null)
        {
            return OperationResult<GitProject>.Failure(MissingDataError);
        }

        if (Accept(input.Folder, out var folder) is { } error)
        {
            return OperationResult<GitProject>.Failure(error);
        }

        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length > GitProject.MaxNameLength)
        {
            return OperationResult<GitProject>.Failure(NameTooLongError);
        }

        var repos = ScanFolder(folder);
        if (repos.Count == 0)
        {
            return OperationResult<GitProject>.Failure(NoReposError);
        }

        var defaultName = GitWorkspaceRules.FolderName(folder);
        var nameIsDefault = name.Length == 0 || string.Equals(name, defaultName, StringComparison.Ordinal);
        var result = _config.SaveGitProject(input.Id, name.Length == 0 ? defaultName : name, nameIsDefault, folder, repos);
        RefreshPinsIf(result.Ok);
        return result;
    }

    /// <summary><c>deleteGitProject</c>.</summary>
    public OperationResult<bool> Delete(string? id)
    {
        var result = _config.DeleteGitProject(id);
        RefreshPinsIf(result.Ok);
        return result;
    }

    /// <summary>
    /// <c>setGitWorkspace</c>: saneado (<see cref="GitWorkspaceRules"/>) e gravado. Repos e pastas que já estavam nas
    /// abas gravadas continuam aceitos; igual por valor ao gravado → não regrava nem refaz a fixação.
    /// </summary>
    public OperationResult<GitWorkspace> SetWorkspace(GitWorkspace? workspace)
    {
        var result = _config.SetGitWorkspace(config =>
        {
            var saved = config.GitWorkspace.Tabs
                .SelectMany(t => t.Repos.Append(t.Folder))
                .Select(FolderPath.Trim)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var context = new WorkspaceContext(_known.Contains, config.GitProjects.Select(p => p.Id).ToHashSet(StringComparer.Ordinal), saved);
            return GitWorkspaceRules.Sanitize(workspace, context);
        });
        if (!result.Ok)
        {
            return OperationResult<GitWorkspace>.Failure(result.Error!, result.Code);
        }

        var (sanitized, changed) = result.Value;
        RefreshPinsIf(changed);
        return OperationResult<GitWorkspace>.Success(sanitized);
    }

    /// <summary><c>projectScripts</c>: nomes dos scripts do package.json de cada repositório da pasta. Nada é executado.</summary>
    public OperationResult<IReadOnlyList<RepoScripts>> Scripts(string? folder)
    {
        if (Accept(folder, out var accepted) is { } error)
        {
            return OperationResult<IReadOnlyList<RepoScripts>>.Failure(error);
        }

        var repos = ScanFolder(accepted);
        return repos.Count == 0
            ? OperationResult<IReadOnlyList<RepoScripts>>.Failure(NoReposError)
            : OperationResult<IReadOnlyList<RepoScripts>>.Success(repos.Select(PackageScripts.Read).ToList());
    }

    /// <summary>
    /// Varre de novo a pasta de cada projeto salvo (repo clonado depois aparece; repo apagado some) e grava se algo
    /// mudou; no máximo a cada 30 s. Pasta que não passa em LocalPath (ex.: unidade removível ausente) fica como está.
    /// Depois refaz a fixação.
    /// </summary>
    public void Rescan()
    {
        lock (_rescanGate)
        {
            var now = _time.GetTimestamp();
            if (_lastRescanTimestamp is { } last && _time.GetElapsedTime(last, now) < RescanInterval)
            {
                return;
            }

            _lastRescanTimestamp = now;
        }

        var scanned = new Dictionary<string, (string Folder, IReadOnlyList<string> Repos)>(StringComparer.Ordinal);
        foreach (var project in _config.Current.GitProjects)
        {
            if (KnownGitRepos.Normalize(project.Folder) is not { } folder)
            {
                continue;
            }

            var repos = ScanFolder(folder);
            if (!repos.SequenceEqual(project.Repos, StringComparer.OrdinalIgnoreCase))
            {
                scanned[project.Id] = (project.Folder, repos);
            }
        }

        if (scanned.Count > 0)
        {
            _config.UpdateGitProjectRepos(scanned); // somente leitura/falha de gravação: segue com o que está gravado
        }

        RefreshPins();
    }

    /// <summary>
    /// Recalcula o conjunto fixado a partir da configuração corrente (projetos + abas), validando cada raiz.
    /// Serializado pela geração da config: só aplica se a config usada ainda é a corrente (senão recalcula) e nunca
    /// troca um conjunto mais novo por um mais velho.
    /// </summary>
    public void RefreshPins()
    {
        while (true)
        {
            var generation = _config.Generation;
            var config = _config.Current;
            var roots = config.GitProjects.SelectMany(p => p.Repos)
                .Concat(config.GitWorkspace.Tabs.SelectMany(t => t.Repos))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(ValidRepoRoot)
                .OfType<string>()
                .ToList();

            lock (_pinGate)
            {
                if (generation != _config.Generation)
                {
                    continue; // a config mudou enquanto calculava: recalcula com a nova
                }

                if (generation >= _pinnedGeneration)
                {
                    _known.SetPinned(roots);
                    _pinnedGeneration = generation;
                }

                return;
            }
        }
    }

    /// <summary>Mesma regra de aceitação de pasta, para outros usos (ex.: <c>termOpen</c>). Devolve a pasta normalizada.</summary>
    public OperationResult<string> AcceptFolder(string? path) =>
        Accept(path, out var folder) is { } error ? OperationResult<string>.Failure(error) : OperationResult<string>.Success(folder);

    /// <summary>Pasta do projeto salvo que contém <paramref name="folder"/> (ela mesma ou uma subpasta), ou null.</summary>
    public string? ProjectFolderOf(string folder)
    {
        var trimmed = FolderPath.Trim(folder);
        return _config.Current.GitProjects
            .Where(p => FolderPath.Same(p.Folder, trimmed)
                || trimmed.StartsWith(FolderPath.Trim(p.Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Folder)
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();
    }

    /// <summary>Normaliza e aplica a regra de aceitação; devolve o erro em português ou null.</summary>
    private string? Accept(string? path, out string folder)
    {
        folder = KnownGitRepos.Normalize(path) ?? string.Empty;
        if (folder.Length == 0)
        {
            return UnknownFolderError;
        }

        if (BroadFolders.IsBroad(folder))
        {
            return BroadFolderError;
        }

        return IsAccepted(folder) ? null : UnknownFolderError;
    }

    /// <summary>
    /// Regra de aceitação de pasta vinda da UI (já normalizada por LocalPath e não ampla), sem link/junção, e: (a) raiz
    /// de repositório conhecido, (b) pasta de trabalho de um processo do último snapshot, (c) mãe direta de repositório
    /// conhecido ou (d) pasta de projeto salvo.
    /// </summary>
    private bool IsAccepted(string folder)
    {
        if (GitRepositoryLocator.IsLink(folder))
        {
            return false;
        }

        return _known.Contains(folder)
            || _processes().Any(p => FolderPath.Same(p.WorkingDirectory, folder))
            || _known.IsParentOfKnown(folder)
            || _config.Current.GitProjects.Any(p => FolderPath.Same(p.Folder, folder));
    }

    /// <summary>Raízes de repositório da pasta: ela mesma, se for raiz; senão os repos das subpastas imediatas.</summary>
    private static IReadOnlyList<string> ScanFolder(string folder)
    {
        if (GitRepositoryLocator.IsLink(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        return IsRepoRoot(folder) ? [folder] : ChildRepos(folder);
    }

    private static List<string> ChildRepos(string folder) =>
        GitRepositoryLocator.FindChildRepos(folder).Select(l => l.RepoRoot).ToList();

    private static bool IsRepoRoot(string folder) =>
        GitRepositoryLocator.Locate(folder) is { } location && FolderPath.Same(location.RepoRoot, folder);

    /// <summary>Raiz normalizada se o caminho for local, não for link/junção e for raiz de repositório de verdade.</summary>
    private static string? ValidRepoRoot(string? path)
    {
        if (KnownGitRepos.Normalize(path) is not { } root || GitRepositoryLocator.IsLink(root) || GitRepositoryLocator.Locate(root) is not { } location
            || !FolderPath.Same(location.RepoRoot, root))
        {
            return null;
        }

        // worktree vinculado: o .git dele precisa apontar de volta para a pasta "worktrees" do repositório principal
        return !GitWorktrees.IsLinkedWorktree(location)
            || (GitWorktrees.CommonDirOf(root) is { } common && GitWorktrees.IsValidLinkedWorktree(root, common))
            ? root
            : null;
    }

    /// <summary>
    /// Status do cache do snapshot só para repositório já conhecido; os demais vêm só com nome e branch lida do HEAD
    /// (sem rodar git num repositório que o app ainda não detectou).
    /// </summary>
    private GitInfo ToInfo(string root)
    {
        if (_known.Contains(root) && _git.Resolve(root, null) is { } info && FolderPath.Same(info.RepoRoot, root))
        {
            return info;
        }

        var branch = GitRepositoryLocator.Locate(root) is { } location ? GitRepositoryLocator.ReadBranch(location.GitDir) : null;
        return new GitInfo(root, GitWorkspaceRules.FolderName(root), branch, null, null, null);
    }

    private void RefreshPinsIf(bool changed)
    {
        if (changed)
        {
            RefreshPins();
        }
    }
}
