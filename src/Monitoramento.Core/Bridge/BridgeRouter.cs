using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Monitoramento.Core.Actions;
using Monitoramento.Core.Git;
using Monitoramento.Core.Launcher;
using Monitoramento.Core.Models;
using Monitoramento.Core.Terminal;

namespace Monitoramento.Core.Bridge;

/// <summary>
/// Ponto único de entrada das mensagens da UI. Recebe <c>{ id, type, payload }</c> e devolve
/// <c>{ replyTo, ok, data | error }</c>. Nunca lança exceção: qualquer falha vira <c>ok: false</c> com erro em português.
/// </summary>
public sealed class BridgeRouter
{
    private const int MaxIconKeys = 500;

    /// <summary>Tamanho máximo de uma mensagem da UI (UTF-8).</summary>
    internal const int MaxMessageBytes = 1024 * 1024;

    private readonly MonitorEngine _engine;

    public BridgeRouter(MonitorEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    private static MonitorJsonContext Json => MonitorJsonContext.Default;

    /// <summary>Processa uma mensagem da UI (fora da thread chamadora) e devolve o JSON de resposta.</summary>
    public Task<string> HandleMessageAsync(string json) => Task.Run(() => HandleAsync(json));

    /// <summary>Mensagem de push: <c>{ "type": "snapshot", "payload": Snapshot }</c>.</summary>
    public string BuildSnapshotMessage(Snapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Write(writer =>
        {
            writer.WriteString("type", "snapshot");
            writer.WritePropertyName("payload");
            JsonSerializer.Serialize(writer, s, Json.Snapshot);
        });
    }

    /// <summary>
    /// Pushes de terminal: <c>{ "type": "termOutput", "payload": { id, data, seq } }</c> e
    /// <c>{ "type": "termExit", "payload": { id, exitCode } }</c>.
    /// </summary>
    internal string BuildTerminalMessage(TerminalEvent terminalEvent)
    {
        ArgumentNullException.ThrowIfNull(terminalEvent);
        return Write(writer =>
        {
            switch (terminalEvent)
            {
                case TerminalOutputEvent output:
                    writer.WriteString("type", "termOutput");
                    writer.WritePropertyName("payload");
                    JsonSerializer.Serialize(writer, new TermOutputPayload(output.Id, output.Data, output.Seq), Json.TermOutputPayload);
                    break;
                case TerminalExitEvent exit:
                    writer.WriteString("type", "termExit");
                    writer.WritePropertyName("payload");
                    JsonSerializer.Serialize(writer, new TermExitPayload(exit.Id, exit.ExitCode), Json.TermExitPayload);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(terminalEvent));
            }
        });
    }

    /// <summary>Recolhe os eventos de terminal pendentes já como mensagens de push (o host posta ou descarta).</summary>
    public IReadOnlyList<string> TakeTerminalMessages() =>
        _engine.Terminals.TakeEvents().Select(BuildTerminalMessage).ToList();

    private async Task<string> HandleAsync(string? json)
    {
        if (json is not null && (json.Length > MaxMessageBytes || Encoding.UTF8.GetByteCount(json) > MaxMessageBytes))
        {
            return Failure(null, "Mensagem grande demais (limite de 1 MB).");
        }

        BridgeRequest request;
        try
        {
            if (!TryParseRequest(json, out request, out var parseError))
            {
                return Failure(request.Id, parseError);
            }
        }
        catch (JsonException)
        {
            return Failure(null, "Mensagem inválida: o JSON está malformado.");
        }

        try
        {
            return await DispatchAsync(request).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Failure(request.Id, $"Dados inválidos para \"{request.Type}\".");
        }
        catch (Exception ex)
        {
            // fronteira com a UI: nenhuma exceção escapa e nenhum texto de exceção (inglês, detalhes internos) vaza
            Trace.TraceError($"Monitoramento: falha ao processar \"{request.Type}\": {ex}");
            return Failure(request.Id, $"Falha ao processar \"{request.Type}\". Tente de novo.");
        }
    }

    private static bool TryParseRequest(string? json, out BridgeRequest request, out string error)
    {
        request = new BridgeRequest(null, null, default);
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Mensagem vazia.";
            return false;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "Mensagem inválida: era esperado um objeto JSON.";
            return false;
        }

        var id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
        var type = root.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() : null;
        var payload = root.TryGetProperty("payload", out var payloadElement) ? payloadElement.Clone() : default;
        request = new BridgeRequest(id, type, payload);

        if (string.IsNullOrWhiteSpace(type))
        {
            error = "Mensagem sem tipo (\"type\").";
            return false;
        }

        return true;
    }

    /// <summary>Mensagens git rodam o git (assíncronas); as demais são síncronas.</summary>
    private Task<string> DispatchAsync(BridgeRequest request) => request.Type switch
    {
        "gitDetails" => GitDetailsAsync(request),
        "gitStage" => GitPathsAsync(request, static (git, p) => git.StageAsync(p.RepoRoot, p.Paths)),
        "gitUnstage" => GitPathsAsync(request, static (git, p) => git.UnstageAsync(p.RepoRoot, p.Paths)),
        "gitDiscard" => GitPathsAsync(request, static (git, p) => git.DiscardAsync(p.RepoRoot, p.Paths)),
        "gitCommit" => GitActionAsync(request, Json.GitCommitPayload, static (git, p) => git.CommitAsync(p.RepoRoot, p.Message, p.All, p.Amend, p.Push)),
        "gitCheckout" => GitActionAsync(request, Json.GitCheckoutPayload, static (git, p) => git.CheckoutAsync(p.RepoRoot, p.Branch, p.Create, p.StartPoint)),
        "gitMerge" => GitActionAsync(request, Json.GitMergePayload, static (git, p) => git.MergeAsync(p.RepoRoot, p.Branch)),
        "gitMergeAbort" => GitRepoAsync(request, static (git, root) => git.MergeAbortAsync(root)),
        "gitFetch" => GitRepoAsync(request, static (git, root) => git.FetchAsync(root)),
        "gitPull" => GitRepoAsync(request, static (git, root) => git.PullAsync(root)),
        "gitPush" => GitRepoAsync(request, static (git, root) => git.PushAsync(root)),
        "gitWorktreeAdd" => GitWorktreeAddAsync(request),
        "gitWorktreeRemove" => GitWorktreeRemoveAsync(request),
        "gitWorktreePrune" => GitRepoAsync(request, static (git, root) => git.PruneWorktreesAsync(root)),
        "termOpen" => TermOpenAsync(request),
        "termRunSavedIn" => TermRunSavedInAsync(request),
        _ => Task.FromResult(Dispatch(request)),
    };

    private string Dispatch(BridgeRequest request) => request.Type switch
    {
        "ready" => Ready(request),
        "toggleFavorite" => ToggleFavorite(request),
        "getIcons" => GetIcons(request),
        "saveProcess" => SaveProcess(request),
        "deleteSaved" => DeleteSaved(request),
        "startSaved" => StartSaved(request),
        "stopSaved" => StopSaved(request),
        "draftFromProcess" => DraftFromProcess(request),
        "killProcess" => KillProcess(request),
        "killGroup" => KillGroup(request),
        "openFolder" => OpenFolder(request),
        "openTerminal" => OpenTerminal(request),
        "setSettings" => SetSettings(request),
        "gitProjectDraft" => GitProjectDraft(request),
        "saveGitProject" => SaveGitProject(request),
        "deleteGitProject" => DeleteGitProject(request),
        "setGitWorkspace" => SetGitWorkspace(request),
        "projectScripts" => ProjectScripts(request),
        "termInput" => TermInput(request),
        "termResize" => TermResize(request),
        "termAttach" => TermAttach(request),
        "termRestart" => TermRestart(request),
        "termClose" => TermClose(request),
        _ => Failure(request.Id, $"Tipo de mensagem desconhecido: \"{request.Type}\"."),
    };

    private string Ready(BridgeRequest request)
    {
        _engine.GitProjects.Rescan(); // projetos salvos: repo clonado depois aparece, repo apagado some
        var snapshot = _engine.LastSnapshot ?? _engine.Sample();
        return Success(request.Id, new ReadyData(snapshot, _engine.Config, _engine.ConfigLoadError), Json.ReadyData);
    }

    private string ToggleFavorite(BridgeRequest request)
    {
        var payload = Payload(request, Json.KeyPayload);
        return Reply(request.Id, _engine.ConfigService.ToggleFavorite(payload.Key), f => new FavoritesData(f), Json.FavoritesData);
    }

    private string GetIcons(BridgeRequest request)
    {
        var payload = Payload(request, Json.KeysPayload);
        var keys = (payload.Keys ?? []).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count > MaxIconKeys)
        {
            return Failure(request.Id, $"Peça no máximo {MaxIconKeys} ícones por vez.");
        }

        var icons = keys.ToDictionary(k => k, k => _engine.Icons.GetDataUrl(k), StringComparer.Ordinal);
        return Success(request.Id, new IconsData(icons), Json.IconsData);
    }

    private string SaveProcess(BridgeRequest request)
    {
        var payload = Payload(request, Json.SavePayload);
        return Reply(request.Id, _engine.ConfigService.SaveProcess(payload.Saved), s => new SavedData(s), Json.SavedData);
    }

    private string DeleteSaved(BridgeRequest request)
    {
        var payload = Payload(request, Json.IdPayload);
        var result = _engine.ConfigService.DeleteSaved(payload.Id);
        if (result.Ok)
        {
            _engine.Launcher.Forget(payload.Id!);
            _engine.Terminals.Unlink(payload.Id!); // a aba fica, sem vínculo: reiniciar pede para fechar
        }

        return Reply(request.Id, result, _ => new EmptyData(), Json.EmptyData);
    }

    private string StartSaved(BridgeRequest request)
    {
        var payload = Payload(request, Json.IdPayload);
        var saved = _engine.ConfigService.FindSaved(payload.Id);
        if (saved is null)
        {
            return Failure(request.Id, "Processo salvo não encontrado.");
        }

        var running = CurrentState(saved.Id)?.Running ?? false;
        return Reply(request.Id, _engine.Launcher.Start(saved, running), s => new StateData(s), Json.StateData);
    }

    private string StopSaved(BridgeRequest request)
    {
        var payload = Payload(request, Json.IdPayload);
        var saved = _engine.ConfigService.FindSaved(payload.Id);
        if (saved is null)
        {
            return Failure(request.Id, "Processo salvo não encontrado.");
        }

        return Reply(request.Id, StopSavedCore(saved), s => new StateData(s), Json.StateData);
    }

    /// <summary>
    /// Para o salvo: coleta nova (só encerra o que está vivo agora, com a identidade pid + CreateTime atual) e, se ele
    /// roda num terminal embutido, encerra a árvore do terminal, que fica parado com o código de saída.
    /// </summary>
    private OperationResult<SavedProcessState> StopSavedCore(SavedProcess saved)
    {
        _engine.Sample();
        var result = _engine.Launcher.Stop(saved, _engine.Config.Saved, _engine.LastProcesses);
        var terminalId = _engine.Terminals.TerminalIdForSaved(saved.Id);
        if (terminalId is not null)
        {
            _engine.Terminals.StopAndWait(terminalId, out _);
            _engine.Sample(); // o próximo snapshot (e um Iniciar logo em seguida) já vê o terminal parado
        }

        return result.Ok ? OperationResult<SavedProcessState>.Success(result.Value! with { TerminalId = terminalId }) : result;
    }

    private string DraftFromProcess(BridgeRequest request)
    {
        var payload = Payload(request, Json.PidPayload);
        if (payload.Pid is not { } pid)
        {
            return Failure(request.Id, "Informe o pid do processo.");
        }

        var process = _engine.LastProcesses.FirstOrDefault(p => p.Pid == pid);
        if (process is null)
        {
            return Failure(request.Id, $"Processo {pid} não encontrado (talvez já tenha terminado).");
        }

        if (string.IsNullOrWhiteSpace(process.CommandLine))
        {
            return Failure(request.Id, "Sem acesso à linha de comando deste processo.");
        }

        var group = _engine.LastSnapshot?.Groups.FirstOrDefault(g => g.Processes.Any(p => p.Pid == pid));
        var draft = new SavedProcess
        {
            Id = string.Empty,
            Name = group?.Name ?? Path.GetFileNameWithoutExtension(process.Name),
            Command = process.CommandLine,
            WorkingDirectory = process.WorkingDirectory,
        };
        var warning = ShellText.HasUnquotedMetacharacters(process.CommandLine)
            ? "A linha de comando tem caracteres especiais do cmd (& | < > ^ %) fora de aspas; ao iniciar, eles serão "
                + "interpretados pelo shell (podem executar outros comandos). Revise o comando antes de salvar."
            : null;
        return Success(request.Id, new DraftData(draft, warning), Json.DraftData);
    }

    private string KillProcess(BridgeRequest request)
    {
        var payload = Payload(request, Json.KillProcessPayload);
        if (payload.Pid is not { } pid)
        {
            return Failure(request.Id, "Informe o pid do processo.");
        }

        var result = _engine.Actions.KillProcess(pid, payload.Tree, _engine.LastProcesses, payload.StartTime);
        return Reply(request.Id, result, k => new KilledData(k), Json.KilledData);
    }

    private string KillGroup(BridgeRequest request)
    {
        var payload = Payload(request, Json.KeyPayload);
        var groups = _engine.LastSnapshot?.Groups ?? [];
        return Reply(request.Id, _engine.Actions.KillGroup(payload.Key, groups), k => new KilledData(k), Json.KilledData);
    }

    private string OpenFolder(BridgeRequest request)
    {
        var payload = Payload(request, Json.PathPayload);
        return Reply(request.Id, _engine.Actions.OpenFolder(payload.Path), _ => new EmptyData(), Json.EmptyData);
    }

    private string OpenTerminal(BridgeRequest request)
    {
        var payload = Payload(request, Json.PathPayload);
        return Reply(request.Id, _engine.Actions.OpenTerminal(payload.Path), _ => new EmptyData(), Json.EmptyData);
    }

    private string SetSettings(BridgeRequest request)
    {
        var payload = Payload(request, Json.SettingsPayload);
        return Reply(request.Id, _engine.ConfigService.SetSettings(payload.Settings), s => new SettingsData(s), Json.SettingsData);
    }

    private string GitProjectDraft(BridgeRequest request)
    {
        var payload = Payload(request, Json.PathPayload);
        return Reply(request.Id, _engine.GitProjects.Draft(payload.Path), d => d, Json.GitProjectDraftData);
    }

    private string SaveGitProject(BridgeRequest request)
    {
        var payload = Payload(request, Json.SaveGitProjectPayload);
        return Reply(request.Id, _engine.GitProjects.Save(payload.Project), p => new GitProjectData(p), Json.GitProjectData);
    }

    private string DeleteGitProject(BridgeRequest request)
    {
        var payload = Payload(request, Json.IdPayload);
        return Reply(request.Id, _engine.GitProjects.Delete(payload.Id), _ => new EmptyData(), Json.EmptyData);
    }

    private string SetGitWorkspace(BridgeRequest request)
    {
        var payload = Payload(request, Json.SetGitWorkspacePayload);
        return Reply(request.Id, _engine.GitProjects.SetWorkspace(payload.Workspace), w => new GitWorkspaceData(w), Json.GitWorkspaceData);
    }

    private string ProjectScripts(BridgeRequest request)
    {
        var payload = Payload(request, Json.FolderPayload);
        return Reply(request.Id, _engine.GitProjects.Scripts(payload.Folder), r => new ProjectScriptsData(r), Json.ProjectScriptsData);
    }

    private async Task<string> TermOpenAsync(BridgeRequest request)
    {
        var payload = Payload(request, Json.TermOpenPayload);
        var folder = _engine.GitProjects.AcceptFolder(payload.Folder);
        if (!folder.Ok)
        {
            return Failure(request.Id, folder.Error!, folder.Code);
        }

        if (payload.ConfirmUnsafe != true && await UnsafeRepoReasonAsync(folder.Value!).ConfigureAwait(false) is { } reason)
        {
            return Failure(request.Id, TerminalErrors.UnsafeRepoPrefix + reason + TerminalErrors.UnsafeRepoSuffix, TerminalErrors.UnsafeRepoCode);
        }

        var result = _engine.Terminals.Open(folder.Value!, payload.Cols, payload.Rows);
        return Reply(request.Id, result, t => new TerminalData(t with { ProjectFolder = _engine.GitProjects.ProjectFolderOf(t.Folder) }), Json.TerminalData);
    }

    /// <summary>
    /// <c>termRunSavedIn</c>: o comando do salvo num terminal, com pasta = worktree + (pasta do salvo relativa à raiz
    /// do repositório dele). Só para worktree (principal ou vinculado validado) do mesmo repositório. Não altera o salvo.
    /// </summary>
    private async Task<string> TermRunSavedInAsync(BridgeRequest request)
    {
        var payload = Payload(request, Json.TermRunSavedInPayload);
        if (_engine.ConfigService.FindSaved(payload.SavedId) is not { } saved)
        {
            return Failure(request.Id, "Processo salvo não encontrado.");
        }

        if (KnownGitRepos.Normalize(saved.WorkingDirectory) is not { } savedFolder || GitRepositoryLocator.Locate(savedFolder) is not { } savedRepo)
        {
            return Failure(request.Id, TerminalErrors.SavedWithoutRepo);
        }

        var mainRoot = GitWorktrees.MainRepoRoot(savedRepo) ?? savedRepo.RepoRoot;
        var worktree = KnownGitRepos.Normalize(payload.WorktreePath);
        var commonDir = GitRepositoryLocator.CommonDir(savedRepo.GitDir) ?? savedRepo.GitDir;
        if (worktree is null || !(FolderPath.Same(worktree, mainRoot) || GitWorktrees.IsValidLinkedWorktree(worktree, commonDir)))
        {
            return Failure(request.Id, TerminalErrors.NotAWorktreeOfSavedRepo);
        }

        var relative = Path.GetRelativePath(savedRepo.RepoRoot, savedFolder);
        var folder = relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? worktree
            : Path.Combine(worktree, relative);
        if (!Directory.Exists(folder))
        {
            return Failure(request.Id, $"A pasta \"{folder}\" não existe neste worktree.");
        }

        if (payload.ConfirmUnsafe != true && await UnsafeRepoReasonAsync(worktree).ConfigureAwait(false) is { } reason)
        {
            return Failure(request.Id, TerminalErrors.UnsafeRepoPrefix + reason + TerminalErrors.UnsafeRepoSuffix, TerminalErrors.UnsafeRepoCode);
        }

        var branch = GitRepositoryLocator.Locate(worktree) is { } location ? GitRepositoryLocator.ReadBranch(location.GitDir) : null;
        var spec = LaunchCommandBuilder.Build(saved with { WorkingDirectory = folder, LaunchMode = LaunchModes.Terminal }, Environment.SystemDirectory);
        var title = $"{saved.Name} ⎇ {branch ?? "HEAD"}";
        var result = _engine.Terminals.OpenCommand(title, folder, new TerminalLaunch(spec.ApplicationName, spec.CommandLine, spec.Environment), payload.Cols, payload.Rows);
        return Reply(request.Id, result, t => new TerminalData(t with { ProjectFolder = _engine.GitProjects.ProjectFolderOf(t.Folder) }), Json.TerminalData);
    }

    /// <summary>
    /// Motivo (sem o prefixo "Status não lido: ") de a config local do repositório da pasta — ou dos repositórios nas
    /// subpastas dela — poder executar programas (fsmonitor, filter, include), pela mesma checagem do status. Null se
    /// seguro ou sem git instalado (sem git, a config não executa nada).
    /// </summary>
    private async Task<string?> UnsafeRepoReasonAsync(string folder)
    {
        if (_engine.GitDetails is null)
        {
            return null;
        }

        IReadOnlyList<GitRepoLocation> repos = GitRepositoryLocator.Locate(folder) is { } inside ? [inside] : GitRepositoryLocator.FindChildRepos(folder);
        foreach (var repo in repos)
        {
            if (await GitRepoSafety.CheckAsync(repo).ConfigureAwait(false) is { } note)
            {
                var reason = note.StartsWith(GitRepoSafety.StatusBlockedPrefix, StringComparison.Ordinal) ? note[GitRepoSafety.StatusBlockedPrefix.Length..] : note;
                return reason.TrimEnd('.');
            }
        }

        return null;
    }

    private async Task<string> GitWorktreeAddAsync(BridgeRequest request)
    {
        var payload = Payload(request, Json.GitWorktreeAddPayload);
        if (_engine.GitActions is not { } git)
        {
            return Failure(request.Id, GitMissingError);
        }

        var add = new GitWorktreeAddRequest(payload.RepoRoot, payload.Branch, payload.NewBranch, payload.StartPoint, payload.Path, payload.CopyEnv == true);
        var result = await git.AddWorktreeAsync(add, _engine.GitProjects.ProjectFolderOf).ConfigureAwait(false);
        return Reply(request.Id, result, r => new GitWorktreeAddData(r.Worktree, r.Details, r.Copied), Json.GitWorktreeAddData);
    }

    private async Task<string> GitWorktreeRemoveAsync(BridgeRequest request)
    {
        var payload = Payload(request, Json.GitWorktreeRemovePayload);
        if (_engine.GitActions is not { } git)
        {
            return Failure(request.Id, GitMissingError);
        }

        _engine.Sample(); // processos atuais: quem está com a pasta dentro do worktree
        var result = await git.RemoveWorktreeAsync(payload.RepoRoot, payload.Path, payload.Force, WorktreeUsers).ConfigureAwait(false);
        return Reply(request.Id, result, r => new GitDetailsData(r.Details), Json.GitDetailsData);
    }

    /// <summary>Terminais do app e processos (último snapshot) com a pasta dentro de <paramref name="worktree"/>.</summary>
    private IReadOnlyList<string> WorktreeUsers(string worktree)
    {
        static bool Inside(string? folder, string root) =>
            folder is not null && (FolderPath.Same(folder, root)
                || FolderPath.Trim(folder).StartsWith(FolderPath.Trim(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        var terminals = _engine.Terminals.AliveTerminals()
            .Where(t => Inside(t.Folder, worktree))
            .Select(t => $"terminal \"{t.Title}\"");
        var processes = _engine.LastProcesses
            .Where(p => p.Pid != _engine.OwnPid && Inside(p.WorkingDirectory, worktree))
            .Select(p => $"{p.Name} (pid {p.Pid})");
        return terminals.Concat(processes).Take(10).ToList();
    }

    private string TermInput(BridgeRequest request)
    {
        var payload = Payload(request, Json.TermInputPayload);
        return Reply(request.Id, _engine.Terminals.Input(payload.Id, payload.Data), _ => new EmptyData(), Json.EmptyData);
    }

    private string TermResize(BridgeRequest request)
    {
        var payload = Payload(request, Json.TermResizePayload);
        return Reply(request.Id, _engine.Terminals.Resize(payload.Id, payload.Cols, payload.Rows), _ => new EmptyData(), Json.EmptyData);
    }

    private string TermAttach(BridgeRequest request)
    {
        var payload = Payload(request, Json.IdPayload);
        return Reply(request.Id, _engine.Terminals.Attach(payload.Id), a => new TermAttachData(a.Data, a.Seq), Json.TermAttachData);
    }

    /// <summary>Reinicia na mesma aba: o shell pelo gerenciador; o processo salvo pelo Parar + Iniciar (mantém o rastreio).</summary>
    private string TermRestart(BridgeRequest request)
    {
        var payload = Payload(request, Json.IdPayload);
        var current = _engine.Terminals.Get(payload.Id, _engine.GitProjects.ProjectFolderOf);
        if (current is null)
        {
            return Failure(request.Id, TerminalManager.NotFoundError);
        }

        if (current.SavedId is { } deletedId && _engine.ConfigService.FindSaved(deletedId) is null)
        {
            return Failure(request.Id, TerminalManager.SavedDeletedError);
        }

        if (current.SavedId is { } savedId && _engine.ConfigService.FindSaved(savedId) is { } saved)
        {
            var stopped = StopSavedCore(saved);
            if (!stopped.Ok)
            {
                return Failure(request.Id, stopped.Error!, stopped.Code);
            }

            var started = _engine.Launcher.Start(saved with { LaunchMode = LaunchModes.Terminal }, alreadyRunning: false);
            if (!started.Ok)
            {
                return Failure(request.Id, started.Error!, started.Code);
            }

            var restarted = _engine.Terminals.Get(started.Value!.TerminalId ?? current.Id, _engine.GitProjects.ProjectFolderOf);
            return restarted is null
                ? Failure(request.Id, TerminalManager.NotFoundError)
                : Success(request.Id, new TerminalData(restarted), Json.TerminalData);
        }

        var result = _engine.Terminals.Restart(payload.Id);
        return Reply(request.Id, result, t => new TerminalData(t with { ProjectFolder = _engine.GitProjects.ProjectFolderOf(t.Folder) }), Json.TerminalData);
    }

    private string TermClose(BridgeRequest request)
    {
        var payload = Payload(request, Json.IdPayload);
        return Reply(request.Id, _engine.Terminals.Close(payload.Id), _ => new EmptyData(), Json.EmptyData);
    }

    private const string GitMissingError = "O git não foi encontrado nesta máquina (instale o Git for Windows).";

    private async Task<string> GitDetailsAsync(BridgeRequest request)
    {
        var payload = Payload(request, Json.GitDetailsPayload);
        if (_engine.GitDetails is not { } git)
        {
            return Failure(request.Id, GitMissingError);
        }

        var result = await git.GetAsync(payload.RepoRoot, payload.MaxCommits, payload.Force == true).ConfigureAwait(false);
        return Reply(request.Id, result, d => new GitDetailsData(d), Json.GitDetailsData);
    }

    private Task<string> GitPathsAsync(BridgeRequest request, Func<GitActionService, GitPathsPayload, Task<OperationResult<GitActionResult>>> action) =>
        GitActionAsync(request, Json.GitPathsPayload, action);

    private Task<string> GitRepoAsync(BridgeRequest request, Func<GitActionService, string?, Task<OperationResult<GitActionResult>>> action) =>
        GitActionAsync(request, Json.GitRepoPayload, (git, p) => action(git, p.RepoRoot));

    private async Task<string> GitActionAsync<TPayload>(
        BridgeRequest request,
        JsonTypeInfo<TPayload> typeInfo,
        Func<GitActionService, TPayload, Task<OperationResult<GitActionResult>>> action)
    {
        var payload = Payload(request, typeInfo);
        if (_engine.GitActions is not { } git)
        {
            return Failure(request.Id, GitMissingError);
        }

        var result = await action(git, payload).ConfigureAwait(false);
        return Reply(request.Id, result, r => new GitActionData(r.Details, r.Output), Json.GitActionData);
    }

    private SavedProcessState? CurrentState(string id) =>
        _engine.LastSnapshot?.Saved.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>Payload ausente ou nulo vale como <c>{}</c>; qualquer outra coisa que não seja objeto é inválida.</summary>
    private static T Payload<T>(BridgeRequest request, JsonTypeInfo<T> typeInfo)
    {
        var element = request.Payload;
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return JsonSerializer.Deserialize("{}", typeInfo)!;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("payload precisa ser um objeto");
        }

        return element.Deserialize(typeInfo) ?? throw new JsonException("payload vazio");
    }

    private static string Reply<TValue, TData>(string? id, OperationResult<TValue> result, Func<TValue, TData> toData, JsonTypeInfo<TData> typeInfo) =>
        result.Ok ? Success(id, toData(result.Value!), typeInfo) : Failure(id, result.Error!, result.Code);

    private static string Success<T>(string? id, T data, JsonTypeInfo<T> typeInfo) => Write(writer =>
    {
        WriteReplyTo(writer, id);
        writer.WriteBoolean("ok", true);
        writer.WritePropertyName("data");
        JsonSerializer.Serialize(writer, data, typeInfo);
    });

    /// <param name="code">Código estável para a UI (ex.: "unsafeRepo"); só sai no JSON quando houver.</param>
    private static string Failure(string? id, string error, string? code = null) => Write(writer =>
    {
        WriteReplyTo(writer, id);
        writer.WriteBoolean("ok", false);
        writer.WriteString("error", error);
        if (code is not null)
        {
            writer.WriteString("code", code);
        }
    });

    private static void WriteReplyTo(Utf8JsonWriter writer, string? id)
    {
        if (id is null)
        {
            writer.WriteNull("replyTo");
        }
        else
        {
            writer.WriteString("replyTo", id);
        }
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
