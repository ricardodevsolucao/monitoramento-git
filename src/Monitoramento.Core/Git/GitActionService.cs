using System.Collections.Concurrent;
using Monitoramento.Core.Actions;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Git;

/// <summary>Resultado de uma ação git: a visão relida logo após a ação e a saída relevante do git (até 4 KB).</summary>
internal sealed record GitActionResult(GitDetails Details, string? Output);

/// <summary>Resultado de <c>gitWorktreeAdd</c>: o worktree criado, a visão relida e os arquivos .env copiados.</summary>
internal sealed record GitWorktreeAddResult(GitWorktree Worktree, GitDetails Details, IReadOnlyList<string> Copied);

/// <summary>Pedido de <c>gitWorktreeAdd</c>.</summary>
internal sealed record GitWorktreeAddRequest(string? RepoRoot, string? Branch, string? NewBranch, string? StartPoint, string? Path, bool CopyEnv);

/// <summary>
/// Ações do painel de controle de código (estilo "Source Control" do VS Code). Rodam como o git do terminal
/// (<see cref="GitRunMode.Action"/>: hooks e assinatura do projeto ativos), porque são intenção explícita do usuário;
/// só aceitam repositório detectado pelo motor, uma ação por repositório por vez, e validam caminhos e branches
/// contra a visão atual antes de chamar o git.
/// </summary>
internal sealed class GitActionService(GitDetailsService details, IGitRunner runner)
{
    public const string BusyError = "Já há uma operação git em andamento neste repositório.";
    public const string EmptyMessageError = "Escreva uma mensagem de commit.";
    public const string RemoveMainWorktreeError = "O worktree principal do repositório não pode ser removido.";
    public const string WorktreeDirtyError =
        "O worktree tem alterações não salvas (arquivos modificados ou não rastreados). Remover mesmo assim descarta essas alterações.";
    public const string WorktreeInUseError = "Feche os terminais/processos rodando no worktree antes de remover.";

    /// <summary>Código de <see cref="WorktreeDirtyError"/> (a UI pede confirmação e repete com force).</summary>
    public const string WorktreeDirtyCode = "worktreeDirty";

    /// <summary>Código de <see cref="WorktreeInUseError"/>.</summary>
    public const string WorktreeInUseCode = "worktreeInUse";
    public static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(180);

    private const int MaxOutputChars = 4 * 1024;
    private const int MaxErrorChars = 1024;
    private const int MaxActionOutputBytes = 1024 * 1024;

    private readonly ConcurrentDictionary<string, byte> _busy = new(StringComparer.OrdinalIgnoreCase);

    public Task<OperationResult<GitActionResult>> StageAsync(string? repoRoot, IReadOnlyList<string?>? paths) =>
        RunExclusiveAsync(repoRoot, async root =>
        {
            if (paths is not { Count: > 0 })
            {
                return Step(await GitAsync(root, LocalTimeout, null, "add", "-A"), "Falha ao preparar as alterações");
            }

            var selected = await SelectChangesAsync(root, paths);
            return selected.Error is { } error
                ? Failed(error)
                : Step(await GitAsync(root, LocalTimeout, null, ["--literal-pathspecs", "add", "--", .. selected.Paths]), "Falha ao preparar as alterações");
        });

    public Task<OperationResult<GitActionResult>> UnstageAsync(string? repoRoot, IReadOnlyList<string?>? paths) =>
        RunExclusiveAsync(repoRoot, async root =>
        {
            if (paths is not { Count: > 0 })
            {
                return Step(await GitAsync(root, LocalTimeout, null, "restore", "--staged", "--", "."), "Falha ao tirar da preparação");
            }

            var selected = await SelectChangesAsync(root, paths);
            if (selected.Error is { } error)
            {
                return Failed(error);
            }

            // renomeação: o caminho antigo volta junto, senão ficaria uma exclusão preparada
            var withOrigins = selected.Changes.SelectMany(c => c.OrigPath is null ? [c.Path] : new[] { c.Path, c.OrigPath }).Distinct(StringComparer.Ordinal);
            return Step(await GitAsync(root, LocalTimeout, null, ["--literal-pathspecs", "restore", "--staged", "--", .. withOrigins]), "Falha ao tirar da preparação");
        });

    public Task<OperationResult<GitActionResult>> DiscardAsync(string? repoRoot, IReadOnlyList<string?>? paths) =>
        RunExclusiveAsync(repoRoot, async root =>
        {
            if (paths is not { Count: > 0 })
            {
                return Failed("Escolha os arquivos cujas alterações serão descartadas.");
            }

            var selected = await SelectChangesAsync(root, paths);
            if (selected.Error is { } error)
            {
                return Failed(error);
            }

            // restore/clean apagam ou reescrevem arquivos: nunca através de junção/link simbólico (sairia do repositório)
            if (selected.Paths.FirstOrDefault(path => PassesThroughLink(root, path)) is { } linked)
            {
                return Failed($"\"{linked}\" passa por um link (junção ou link simbólico); o descarte foi recusado por segurança.");
            }

            const string title = "Falha ao descartar as alterações";
            var tracked = selected.Changes.Where(c => c.Worktree != GitWorktreeState.Untracked).Select(c => c.Path).ToList();
            var untracked = selected.Changes.Where(c => c.Worktree == GitWorktreeState.Untracked).Select(c => c.Path).ToList();
            var outputs = new List<string?>();
            if (tracked.Count > 0)
            {
                var restored = Step(await GitAsync(root, LocalTimeout, null, ["--literal-pathspecs", "restore", "--worktree", "--", .. tracked]), title);
                if (restored.Error is not null)
                {
                    return restored;
                }

                outputs.Add(restored.Output);
            }

            if (untracked.Count > 0)
            {
                var cleaned = Step(await GitAsync(root, LocalTimeout, null, ["--literal-pathspecs", "clean", "-f", "-d", "--", .. untracked]), title);
                if (cleaned.Error is not null)
                {
                    return cleaned;
                }

                outputs.Add(cleaned.Output);
            }

            return new StepResult(null, Join(outputs));
        });

    public Task<OperationResult<GitActionResult>> CommitAsync(string? repoRoot, string? message, bool all, bool amend, bool push)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return Task.FromResult(OperationResult<GitActionResult>.Failure(EmptyMessageError));
        }

        return RunExclusiveAsync(repoRoot, async root =>
        {
            if (all)
            {
                var staged = Step(await GitAsync(root, LocalTimeout, null, "add", "-A"), "Falha ao preparar as alterações");
                if (staged.Error is not null)
                {
                    return staged;
                }
            }

            string[] args = amend ? ["commit", "--amend", "-F", "-"] : ["commit", "-F", "-"];
            var committed = Step(await GitAsync(root, LocalTimeout, message, args), "Falha ao fazer commit");
            if (committed.Error is not null || !push)
            {
                return committed;
            }

            var pushed = await PushCoreAsync(root, "O commit foi feito, mas o envio (push) falhou");
            return pushed with { Output = Join([committed.Output, pushed.Output]) };
        });
    }

    public Task<OperationResult<GitActionResult>> CheckoutAsync(string? repoRoot, string? branch, bool create, string? startPoint) =>
        RunExclusiveAsync(repoRoot, async root =>
        {
            if (string.IsNullOrWhiteSpace(branch))
            {
                return Failed("Informe o nome da branch.");
            }

            var current = await CurrentAsync(root);
            if (current.Error is { } readError)
            {
                return Failed(readError);
            }

            var branches = current.Details!.Branches;
            if (create)
            {
                if (!await IsValidNewBranchNameAsync(root, branch))
                {
                    return Failed($"Nome de branch inválido: \"{branch}\".");
                }

                if (branches.Any(b => !b.Remote && b.Name == branch))
                {
                    return Failed($"Já existe uma branch \"{branch}\".");
                }

                if (string.IsNullOrEmpty(startPoint))
                {
                    return Step(await GitAsync(root, LocalTimeout, null, "switch", "-c", branch), "Falha ao criar a branch"); // nome já validado
                }

                return branches.Any(b => b.Name == startPoint)
                    ? Step(await GitAsync(root, LocalTimeout, null, "switch", "-c", branch, "--end-of-options", startPoint), "Falha ao criar a branch")
                    : Failed($"Ponto de partida não encontrado entre as branches: \"{startPoint}\".");
            }

            const string title = "Falha ao trocar de branch";
            if (branches.Any(b => !b.Remote && b.Name == branch))
            {
                return Step(await GitAsync(root, LocalTimeout, null, "switch", "--end-of-options", branch), title);
            }

            if (branches.FirstOrDefault(b => b.Remote && b.Name == branch) is { } remote)
            {
                // "origin/x": se já existir a local "x", só troca; senão cria acompanhando a remota
                var slash = remote.Name.IndexOf('/', StringComparison.Ordinal);
                var local = slash >= 0 ? remote.Name[(slash + 1)..] : remote.Name;
                return branches.Any(b => !b.Remote && b.Name == local)
                    ? Step(await GitAsync(root, LocalTimeout, null, "switch", "--end-of-options", local), title)
                    : Step(await GitAsync(root, LocalTimeout, null, "switch", "--track", "--end-of-options", remote.Name), title);
            }

            return Failed($"Branch não encontrada: \"{branch}\".");
        });

    public Task<OperationResult<GitActionResult>> MergeAsync(string? repoRoot, string? branch) =>
        RunExclusiveAsync(repoRoot, async root =>
        {
            var current = await CurrentAsync(root);
            if (current.Error is { } readError)
            {
                return Failed(readError);
            }

            if (string.IsNullOrWhiteSpace(branch) || !current.Details!.Branches.Any(b => b.Name == branch && !b.Current))
            {
                return Failed($"Branch não encontrada para o merge: \"{branch}\".");
            }

            var result = await GitAsync(root, LocalTimeout, null, "merge", "--no-edit", "--end-of-options", branch);
            if (!result.Ok && File.Exists(Path.Combine(GitDirOf(root), "MERGE_HEAD")))
            {
                // conflito: não é erro da ação; a visão volta com merging=true e as mudanças em conflito
                return new StepResult(null, CombinedOutput(result));
            }

            return Step(result, "Falha ao fazer merge");
        });

    public Task<OperationResult<GitActionResult>> MergeAbortAsync(string? repoRoot) =>
        RunExclusiveAsync(repoRoot, async root => Step(await GitAsync(root, LocalTimeout, null, "merge", "--abort"), "Falha ao cancelar o merge"));

    public Task<OperationResult<GitActionResult>> FetchAsync(string? repoRoot) =>
        RunExclusiveAsync(repoRoot, async root => Step(await GitAsync(root, NetworkTimeout, null, "fetch", "--prune"), "Falha ao buscar do remoto (fetch)"));

    public Task<OperationResult<GitActionResult>> PullAsync(string? repoRoot) =>
        RunExclusiveAsync(repoRoot, async root => Step(
            await GitAsync(root, NetworkTimeout, null, "pull", "--ff-only"),
            "Falha ao atualizar (pull só avança a branch; se ela divergiu do remoto, faça fetch e merge manualmente)"));

    /// <summary>
    /// <c>gitWorktreeAdd</c>: branch existente (local, ou remota → cria a local acompanhando) ou branch nova a partir de
    /// <c>startPoint</c> (padrão HEAD), num caminho validado (<see cref="GitWorktreePaths"/>). Roda como ação
    /// (hooks ativos). O worktree criado é validado, registrado e fixado em <see cref="KnownGitRepos"/>.
    /// </summary>
    public async Task<OperationResult<GitWorktreeAddResult>> AddWorktreeAsync(GitWorktreeAddRequest request, Func<string, string?> projectFolderOf)
    {
        if (string.IsNullOrWhiteSpace(request.Branch) == string.IsNullOrWhiteSpace(request.NewBranch))
        {
            return OperationResult<GitWorktreeAddResult>.Failure("Escolha uma branch existente ou informe o nome de uma branch nova (só um dos dois).");
        }

        string? created = null;
        var result = await RunExclusiveAsync(request.RepoRoot, async root =>
        {
            var current = await CurrentAsync(root);
            if (current.Error is { } readError)
            {
                return Failed(readError);
            }

            var d = current.Details!;
            var mainRoot = d.Repo.MainRepoRoot ?? root;
            var args = new List<string> { "worktree", "add" };
            string? commitish;
            string slugSource;
            if (!string.IsNullOrWhiteSpace(request.NewBranch))
            {
                var newBranch = request.NewBranch.Trim();
                if (!await IsValidNewBranchNameAsync(root, newBranch))
                {
                    return Failed($"Nome de branch inválido: \"{newBranch}\".");
                }

                if (d.Branches.Any(b => !b.Remote && b.Name == newBranch))
                {
                    return Failed($"Já existe uma branch \"{newBranch}\".");
                }

                if (!string.IsNullOrEmpty(request.StartPoint) && !d.Branches.Any(b => b.Name == request.StartPoint))
                {
                    return Failed($"Ponto de partida não encontrado entre as branches: \"{request.StartPoint}\".");
                }

                args.AddRange(["-b", newBranch]);
                commitish = string.IsNullOrEmpty(request.StartPoint) ? null : request.StartPoint;
                slugSource = newBranch;
            }
            else
            {
                var branch = request.Branch!.Trim();
                var local = d.Branches.FirstOrDefault(b => !b.Remote && b.Name == branch);
                if (local is null && d.Branches.FirstOrDefault(b => b.Remote && b.Name == branch) is { } remote)
                {
                    var slash = remote.Name.IndexOf('/', StringComparison.Ordinal);
                    var localName = slash >= 0 ? remote.Name[(slash + 1)..] : remote.Name;
                    local = d.Branches.FirstOrDefault(b => !b.Remote && b.Name == localName);
                    if (local is null)
                    {
                        if (!await IsValidNewBranchNameAsync(root, localName))
                        {
                            return Failed($"Nome de branch inválido: \"{localName}\".");
                        }

                        args.AddRange(["--track", "-b", localName]);
                        branch = remote.Name;
                    }
                    else
                    {
                        branch = local.Name;
                    }
                }
                else if (local is null)
                {
                    return Failed($"Branch não encontrada: \"{branch}\".");
                }

                if (local is not null)
                {
                    var inUse = local.Current ? root : local.WorktreePath;
                    if (inUse is not null)
                    {
                        return Failed($"A branch \"{local.Name}\" já está em uso no worktree \"{inUse}\".");
                    }
                }

                commitish = branch;
                slugSource = local?.Name ?? args[^1];
            }

            var (path, pathError) = string.IsNullOrWhiteSpace(request.Path)
                ? GitWorktreePaths.Default(mainRoot, slugSource, projectFolderOf)
                : GitWorktreePaths.Validate(request.Path);
            if (pathError is not null)
            {
                return Failed(pathError);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path!)!); // a ".worktrees" padrão
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failed(GitWorktreePaths.PathPrefix + "não foi possível criar a pasta-mãe.");
            }

            args.AddRange(["--end-of-options", path!]);
            if (commitish is not null)
            {
                args.Add(commitish);
            }

            var step = Step(await GitAsync(root, LocalTimeout, null, [.. args]), "Falha ao criar o worktree");
            if (step.Error is null)
            {
                created = path;
            }

            return step;
        }).ConfigureAwait(false);

        if (!result.Ok)
        {
            return OperationResult<GitWorktreeAddResult>.Failure(result.Error!, result.Code);
        }

        var root = details.ResolveRepo(request.RepoRoot).Value!;
        var mainRoot = result.Value!.Details.Repo.MainRepoRoot ?? root;
        if (created is null || GitWorktrees.CommonDirOf(mainRoot) is not { } commonDir || !GitWorktrees.IsValidLinkedWorktree(created, commonDir))
        {
            return OperationResult<GitWorktreeAddResult>.Failure("O worktree foi criado, mas não pôde ser validado; atualize o painel.");
        }

        details.Known.Register(created);
        details.Known.Pin(created);
        var copied = request.CopyEnv ? await CopyEnvAsync(mainRoot, created).ConfigureAwait(false) : [];
        var worktree = result.Value.Details.Worktrees.FirstOrDefault(w => FolderPath.Same(w.Path, created))
            ?? new GitWorktree(created, null, string.Empty, false, false, false, null, false, null);
        return OperationResult<GitWorktreeAddResult>.Success(new GitWorktreeAddResult(worktree, result.Value.Details, copied));
    }

    /// <summary>
    /// <c>gitWorktreeRemove</c>: nunca o principal; recusa se houver terminal do app ou processo com pasta dentro
    /// (<paramref name="usersOf"/>); sem <paramref name="force"/> e com mudanças → <see cref="WorktreeDirtyError"/>.
    /// </summary>
    public async Task<OperationResult<GitActionResult>> RemoveWorktreeAsync(string? repoRoot, string? path, bool force, Func<string, IReadOnlyList<string>> usersOf)
    {
        var target = KnownGitRepos.Normalize(path);
        var result = await RunExclusiveAsync(repoRoot, async root =>
        {
            if (target is null)
            {
                return Failed("Worktree não encontrado neste repositório.");
            }

            var current = await CurrentAsync(root);
            if (current.Error is { } readError)
            {
                return Failed(readError);
            }

            var worktree = current.Details!.Worktrees.FirstOrDefault(w => FolderPath.Same(w.Path, target));
            if (worktree is null)
            {
                return Failed("Worktree não encontrado neste repositório.");
            }

            if (worktree.IsMain)
            {
                return Failed(RemoveMainWorktreeError);
            }

            if (FolderPath.Same(root, target))
            {
                return Failed("Abra o repositório principal (ou outro worktree) para remover este worktree.");
            }

            var users = usersOf(target);
            if (users.Count > 0)
            {
                return Failed($"{WorktreeInUseError} Em uso por: {string.Join(", ", users)}.", WorktreeInUseCode);
            }

            var args = new List<string> { "worktree", "remove" };
            if (force)
            {
                args.Add("--force");
                if (worktree.Locked)
                {
                    args.Add("--force"); // trancado: o git pede -f duas vezes
                }
            }

            args.AddRange(["--end-of-options", worktree.Path]);
            var removed = await GitAsync(root, LocalTimeout, null, [.. args]);
            if (!removed.Ok && !force && !removed.TimedOut && removed.Error.Contains("--force", StringComparison.Ordinal))
            {
                return Failed(WorktreeDirtyError, WorktreeDirtyCode);
            }

            return Step(removed, "Falha ao remover o worktree");
        }).ConfigureAwait(false);

        if (result.Ok && target is not null)
        {
            details.Known.Unpin(target);
            details.Invalidate(target);
        }

        return result;
    }

    /// <summary><c>gitWorktreePrune</c>: limpa os registros de worktrees cuja pasta sumiu (e tira a fixação deles).</summary>
    public async Task<OperationResult<GitActionResult>> PruneWorktreesAsync(string? repoRoot)
    {
        IReadOnlyList<string> prunable = [];
        var result = await RunExclusiveAsync(repoRoot, async root =>
        {
            var current = await CurrentAsync(root);
            prunable = current.Details?.Worktrees.Where(w => w.Prunable).Select(w => w.Path).ToList() ?? [];
            return Step(await GitAsync(root, LocalTimeout, null, "worktree", "prune"), "Falha ao limpar os worktrees");
        }).ConfigureAwait(false);

        if (result.Ok)
        {
            foreach (var path in prunable)
            {
                details.Known.Unpin(path);
            }
        }

        return result;
    }

    /// <summary>Copia os .env não versionados do principal (ls-files diz quais são versionados).</summary>
    private async Task<IReadOnlyList<string>> CopyEnvAsync(string mainRoot, string worktreeRoot)
    {
        var candidates = GitWorktreePaths.EnvCandidates(mainRoot);
        if (candidates.Count == 0)
        {
            return [];
        }

        var listed = await runner.RunAsync(new GitRunRequest(mainRoot, ["--literal-pathspecs", "ls-files", "-z", "--", .. candidates], GitRunMode.Read)).ConfigureAwait(false);
        if (!listed.Ok)
        {
            return []; // sem saber o que é versionado, não copia nada
        }

        var tracked = listed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return GitWorktreePaths.CopyEnvFiles(mainRoot, worktreeRoot, tracked);
    }

    public Task<OperationResult<GitActionResult>> PushAsync(string? repoRoot) =>
        RunExclusiveAsync(repoRoot, root => PushCoreAsync(root, "Falha ao enviar (push)"));

    private async Task<StepResult> PushCoreAsync(string root, string title)
    {
        var current = await CurrentAsync(root);
        if (current.Error is { } readError)
        {
            return Failed(readError);
        }

        var d = current.Details!;
        if (d.Head.Branch is null || d.Head.Detached)
        {
            return Failed($"{title}: não há branch atual (HEAD destacado).");
        }

        if (d.Head.Upstream is not null)
        {
            return Step(await GitAsync(root, NetworkTimeout, null, "push"), title);
        }

        return d.Remotes.Count switch
        {
            0 => Failed($"{title}: nenhum remoto configurado neste repositório."),
            1 when d.Remotes[0].StartsWith('-') => Failed($"{title}: nome de remoto inválido."),
            1 => Step(await GitAsync(root, NetworkTimeout, null, "push", "-u", "--end-of-options", d.Remotes[0], "HEAD"), title),
            _ => Failed($"{title}: a branch não tem upstream e há mais de um remoto; configure o upstream no terminal."),
        };
    }

    private async Task<OperationResult<GitActionResult>> RunExclusiveAsync(string? repoRoot, Func<string, Task<StepResult>> action)
    {
        var resolved = details.ResolveRepo(repoRoot);
        if (!resolved.Ok)
        {
            return OperationResult<GitActionResult>.Failure(resolved.Error!, resolved.Code);
        }

        var root = resolved.Value!;
        if (!_busy.TryAdd(root, 0))
        {
            return OperationResult<GitActionResult>.Failure(BusyError);
        }

        try
        {
            var step = await action(root).ConfigureAwait(false);
            if (step.Error is not null)
            {
                details.Invalidate(root); // a ação pode ter mudado algo antes de falhar
                return OperationResult<GitActionResult>.Failure(step.Error, step.Code);
            }

            var refreshed = await details.RefreshAsync(root).ConfigureAwait(false);
            return refreshed.Ok
                ? OperationResult<GitActionResult>.Success(new GitActionResult(refreshed.Value!, step.Output))
                : OperationResult<GitActionResult>.Failure(refreshed.Error!, refreshed.Code);
        }
        finally
        {
            _busy.TryRemove(root, out _);
        }
    }

    private Task<GitRunResult> GitAsync(string root, TimeSpan timeout, string? input, params string[] args) =>
        runner.RunAsync(new GitRunRequest(root, args, GitRunMode.Action, timeout, MaxActionOutputBytes, input));

    /// <summary>Visão atual (sem cache) para validar caminhos/branches contra o que o git vê agora.</summary>
    private async Task<(GitDetails? Details, string? Error)> CurrentAsync(string root)
    {
        var current = await details.RefreshAsync(root).ConfigureAwait(false);
        return current.Ok ? (current.Value, null) : (null, current.Error);
    }

    /// <summary>Caminhos relativos, dentro do repositório e presentes na lista de mudanças atual.</summary>
    private async Task<(IReadOnlyList<GitChange> Changes, IReadOnlyList<string> Paths, string? Error)> SelectChangesAsync(string root, IReadOnlyList<string?> paths)
    {
        foreach (var path in paths)
        {
            if (!IsSafeRelativePath(path))
            {
                return ([], [], $"Caminho inválido: \"{path}\". Use caminhos relativos ao repositório, sem \"..\".");
            }
        }

        var current = await CurrentAsync(root);
        if (current.Error is { } readError)
        {
            return ([], [], readError);
        }

        if (current.Details!.Changes is not { } changes)
        {
            return ([], [], "As mudanças deste repositório não puderam ser lidas; a ação foi cancelada.");
        }

        var selected = new List<GitChange>();
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            var change = changes.FirstOrDefault(c => c.Path == path);
            if (change is null)
            {
                return ([], [], $"\"{path}\" não está na lista de mudanças; atualize o painel.");
            }

            selected.Add(change);
        }

        return (selected, selected.Select(c => c.Path).ToList(), null);
    }

    internal static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > 4096
            || path[0] is '/' or '\\'
            || path.Contains(':', StringComparison.Ordinal)
            || path.Any(char.IsControl)
            || Path.IsPathRooted(path))
        {
            return false;
        }

        return !path.Split('/', '\\').Any(segment => segment == "..");
    }

    /// <summary>Algum segmento entre a raiz e o arquivo (inclusive) é junção/link simbólico.</summary>
    private static bool PassesThroughLink(string root, string relative)
    {
        var current = root;
        foreach (var segment in relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (GitRepositoryLocator.IsLink(current))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>git check-ref-format --branch</c> precisa aceitar o nome e devolvê-lo igual (recusa "@{-1}" etc.).</summary>
    private async Task<bool> IsValidNewBranchNameAsync(string root, string name)
    {
        if (name.StartsWith('-') || name.Any(char.IsWhiteSpace) || name.Any(char.IsControl))
        {
            return false;
        }

        var result = await runner.RunAsync(new GitRunRequest(root, ["check-ref-format", "--branch", name], GitRunMode.Read)).ConfigureAwait(false);
        return result.Ok && result.Output.Trim() == name;
    }

    private static string GitDirOf(string root) =>
        GitRepositoryLocator.Locate(root)?.GitDir ?? Path.Combine(root, ".git");

    private static StepResult Step(GitRunResult result, string title)
    {
        if (result.Ok)
        {
            return new StepResult(null, CombinedOutput(result));
        }

        if (result.TimedOut)
        {
            return Failed($"{title}: o git não terminou a tempo e foi interrompido.");
        }

        if (result.StartFailed)
        {
            return Failed($"{title}: não foi possível executar o git.");
        }

        var message = Cut(result.Error.Trim().Length > 0 ? result.Error : result.Output, MaxErrorChars);
        return Failed(message.Length > 0 ? $"{title}: {message}" : $"{title}.");
    }

    private static StepResult Failed(string error, string? code = null) => new(error, null, code);

    private static string? CombinedOutput(GitRunResult result) => Join([result.Output, result.Error]);

    private static string? Join(IEnumerable<string?> parts)
    {
        var text = string.Join('\n', parts.Select(p => p?.Trim()).Where(p => !string.IsNullOrEmpty(p)));
        return text.Length == 0 ? null : Cut(text, MaxOutputChars);
    }

    private static string Cut(string text, int max)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    /// <param name="Code">Código estável do erro para a UI (ex.: "worktreeDirty"), propagado até a resposta.</param>
    private sealed record StepResult(string? Error, string? Output, string? Code = null);
}
