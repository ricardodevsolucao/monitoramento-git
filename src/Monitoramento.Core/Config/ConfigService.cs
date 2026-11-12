using System.Diagnostics;
using Monitoramento.Core.Actions;
using Monitoramento.Core.Favorites;
using Monitoramento.Core.Git;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Config;

/// <summary>
/// Configuração corrente em memória + persistência. Toda alteração grava antes de virar a corrente e é validada
/// dentro do lock (contra o estado atual). Se o arquivo existe mas não pôde ser lido, entra em modo somente leitura:
/// alterações são recusadas até uma releitura funcionar, e o arquivo nunca é sobrescrito com os padrões.
/// </summary>
internal sealed class ConfigService
{
    private const string NotFound = "Processo salvo não encontrado.";

    private readonly ConfigStore _store;
    private readonly Lock _gate = new();
    private AppConfig _current;
    private volatile string? _loadError;
    private bool _readOnly;
    private long _generation;

    public ConfigService(ConfigStore store)
    {
        _store = store;
        _current = store.Load();
        _loadError = store.LastLoadError;
        _readOnly = store.LastLoadFailed;
    }

    public AppConfig Current => Volatile.Read(ref _current);

    /// <summary>Aumenta a cada troca da configuração corrente (gravação ou releitura); igual = nada mudou.</summary>
    internal long Generation => Interlocked.Read(ref _generation);

    /// <summary>Motivo pelo qual a configuração não pôde ser lida, ou null.</summary>
    public string? LoadError => _loadError;

    public SavedProcess? FindSaved(string? id) =>
        id is null ? null : Current.Saved.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    public OperationResult<IReadOnlyList<string>> ToggleFavorite(string? key)
    {
        if (!FavoriteKeys.IsValid(key))
        {
            return OperationResult<IReadOnlyList<string>>.Failure("Chave de favorito inválida.");
        }

        return Mutate(config =>
        {
            IReadOnlyList<string> favorites = config.Favorites.Contains(key!, StringComparer.Ordinal)
                ? config.Favorites.Where(f => !string.Equals(f, key, StringComparison.Ordinal)).ToList()
                : [.. config.Favorites, key!];
            return Change(config with { Favorites = favorites }, favorites);
        });
    }

    public OperationResult<SavedProcess> SaveProcess(SavedProcess? input)
    {
        if (input is null)
        {
            return OperationResult<SavedProcess>.Failure("Dados do processo salvo ausentes.");
        }

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return OperationResult<SavedProcess>.Failure("Informe um nome para o processo.");
        }

        if (string.IsNullOrWhiteSpace(input.Command))
        {
            return OperationResult<SavedProcess>.Failure("Informe o comando a executar.");
        }

        var env = NormalizeEnv(input.Env);
        if (!env.Ok)
        {
            return OperationResult<SavedProcess>.Failure(env.Error!, env.Code);
        }

        var isNew = string.IsNullOrWhiteSpace(input.Id);
        var saved = new SavedProcess
        {
            Id = isNew ? Guid.NewGuid().ToString() : input.Id,
            Name = input.Name.Trim(),
            Command = input.Command.Trim(),
            WorkingDirectory = string.IsNullOrWhiteSpace(input.WorkingDirectory) ? null : input.WorkingDirectory.Trim(),
            Env = env.Value!,
            LaunchMode = LaunchModes.Normalize(input.LaunchMode),
            Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes,
        };

        return Mutate(config =>
        {
            if (isNew)
            {
                return Change(config with { Saved = [.. config.Saved, saved] }, saved);
            }

            // revalida dentro do lock: o id pode ter sido apagado por outra mensagem
            if (!config.Saved.Any(s => string.Equals(s.Id, saved.Id, StringComparison.Ordinal)))
            {
                return OperationResult<(AppConfig, SavedProcess)>.Failure(NotFound);
            }

            var list = config.Saved.Select(s => string.Equals(s.Id, saved.Id, StringComparison.Ordinal) ? saved : s).ToList();
            return Change(config with { Saved = list }, saved);
        });
    }

    public OperationResult<bool> DeleteSaved(string? id)
    {
        if (id is null)
        {
            return OperationResult<bool>.Failure(NotFound);
        }

        return Mutate(config =>
        {
            if (!config.Saved.Any(s => string.Equals(s.Id, id, StringComparison.Ordinal)))
            {
                return OperationResult<(AppConfig, bool)>.Failure(NotFound);
            }

            return Change(config with { Saved = config.Saved.Where(s => !string.Equals(s.Id, id, StringComparison.Ordinal)).ToList() }, true);
        });
    }

    public OperationResult<Settings> SetSettings(Settings? settings)
    {
        var error = SettingsValidator.Validate(settings);
        if (error is not null)
        {
            return OperationResult<Settings>.Failure(error);
        }

        return Mutate(config => Change(config with { Settings = settings! }, settings!));
    }

    /// <summary>
    /// Grava um projeto git já validado (pasta aceita, repos recalculados). Id vazio = novo, mas pasta já salva atualiza
    /// o projeto existente em vez de duplicar. Limite de <see cref="GitProject.MaxProjects"/>.
    /// </summary>
    /// <param name="nameIsDefault">
    /// O nome enviado é o padrão (nome da pasta): id vazio com pasta já salva mantém o nome atual do projeto.
    /// </param>
    public OperationResult<GitProject> SaveGitProject(string? id, string name, bool nameIsDefault, string folder, IReadOnlyList<string> repos)
    {
        var isNew = string.IsNullOrWhiteSpace(id);
        return Mutate(config =>
        {
            var sameFolder = config.GitProjects.FirstOrDefault(p => FolderPath.Same(p.Folder, folder));
            var effectiveName = isNew && nameIsDefault && sameFolder is not null ? sameFolder.Name : name;

            GitProject target;
            if (isNew)
            {
                if (sameFolder is null && config.GitProjects.Count >= GitProject.MaxProjects)
                {
                    return OperationResult<(AppConfig, GitProject)>.Failure(
                        $"Limite de {GitProject.MaxProjects} projetos salvos atingido. Apague um para salvar outro.");
                }

                target = sameFolder ?? new GitProject { Id = Guid.NewGuid().ToString() };
            }
            else
            {
                // revalida dentro do lock: o id pode ter sido apagado por outra mensagem
                var existing = config.GitProjects.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
                if (existing is null)
                {
                    return OperationResult<(AppConfig, GitProject)>.Failure(GitProjectNotFound);
                }

                target = existing;

                if (sameFolder is not null && !string.Equals(sameFolder.Id, id, StringComparison.Ordinal))
                {
                    return OperationResult<(AppConfig, GitProject)>.Failure("Já existe outro projeto salvo com essa pasta.");
                }
            }

            var project = target with { Name = effectiveName, Folder = folder, Repos = repos };
            var exists = config.GitProjects.Any(p => string.Equals(p.Id, project.Id, StringComparison.Ordinal));
            IReadOnlyList<GitProject> list = exists
                ? config.GitProjects.Select(p => string.Equals(p.Id, project.Id, StringComparison.Ordinal) ? project : p).ToList()
                : [.. config.GitProjects, project];
            return Change(config with { GitProjects = list }, project);
        });
    }

    /// <summary>Apaga o projeto; abas do workspace que apontavam para ele ficam com <c>projectId</c> null.</summary>
    public OperationResult<bool> DeleteGitProject(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return OperationResult<bool>.Failure(GitProjectNotFound);
        }

        return Mutate(config =>
        {
            if (!config.GitProjects.Any(p => string.Equals(p.Id, id, StringComparison.Ordinal)))
            {
                return OperationResult<(AppConfig, bool)>.Failure(GitProjectNotFound);
            }

            var tabs = config.GitWorkspace.Tabs
                .Select(t => string.Equals(t.ProjectId, id, StringComparison.Ordinal) ? t with { ProjectId = null } : t)
                .ToList();
            return Change(
                config with
                {
                    GitProjects = config.GitProjects.Where(p => !string.Equals(p.Id, id, StringComparison.Ordinal)).ToList(),
                    GitWorkspace = config.GitWorkspace with { Tabs = tabs },
                },
                true);
        });
    }

    /// <param name="sanitize">Saneia contra a configuração corrente (dentro do lock: ids de projeto e abas atuais).</param>
    /// <returns>O workspace gravado e se ele mudou (igual por valor → não regrava o arquivo).</returns>
    public OperationResult<(GitWorkspace Workspace, bool Changed)> SetGitWorkspace(Func<AppConfig, GitWorkspace> sanitize) =>
        Mutate(config =>
        {
            var workspace = sanitize(config);
            return GitWorkspaceRules.SameValue(workspace, config.GitWorkspace)
                ? Change(config, (config.GitWorkspace, false))
                : Change(config with { GitWorkspace = workspace }, (workspace, true));
        });

    /// <summary>
    /// Resultado de uma nova varredura das pastas dos projetos (id → pasta varrida e repos achados). Só aplica onde a
    /// pasta não mudou nesse meio-tempo; não grava nada se nenhum projeto mudou.
    /// </summary>
    /// <returns>true se algum projeto mudou (e foi gravado).</returns>
    public OperationResult<bool> UpdateGitProjectRepos(IReadOnlyDictionary<string, (string Folder, IReadOnlyList<string> Repos)> scanned) =>
        Mutate(config =>
        {
            var changed = false;
            var list = config.GitProjects.Select(p =>
            {
                if (!scanned.TryGetValue(p.Id, out var scan) || !FolderPath.Same(p.Folder, scan.Folder)
                    || p.Repos.SequenceEqual(scan.Repos, StringComparer.OrdinalIgnoreCase))
                {
                    return p;
                }

                changed = true;
                return p with { Repos = scan.Repos };
            }).ToList();
            return changed ? Change(config with { GitProjects = list }, true) : Change(config, false);
        });

    private const string GitProjectNotFound = "Projeto não encontrado.";

    /// <summary>Chaves aparadas, sem diferenciar maiúsculas (como no Windows); repetidas: a última vence.</summary>
    private static OperationResult<IReadOnlyDictionary<string, string>> NormalizeEnv(IReadOnlyDictionary<string, string>? env)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawKey, value) in env ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                continue;
            }

            var key = rawKey.Trim();
            if (key.Contains('=', StringComparison.Ordinal) || key.Contains('\0', StringComparison.Ordinal))
            {
                return OperationResult<IReadOnlyDictionary<string, string>>.Failure($"Nome de variável de ambiente inválido: \"{key}\".");
            }

            result[key] = value ?? string.Empty;
        }

        return OperationResult<IReadOnlyDictionary<string, string>>.Success(result);
    }

    private static OperationResult<(AppConfig Config, T Result)> Change<T>(AppConfig config, T result) =>
        OperationResult<(AppConfig, T)>.Success((config, result));

    private OperationResult<T> Mutate<T>(Func<AppConfig, OperationResult<(AppConfig Config, T Result)>> change)
    {
        lock (_gate)
        {
            if (_readOnly && !TryReload())
            {
                return OperationResult<T>.Failure(
                    "A configuração não pôde ser lida (o arquivo pode estar em uso por outro programa). "
                    + "Para não sobrescrevê-la, nenhuma alteração foi gravada; tente de novo em instantes.");
            }

            var outcome = change(_current);
            if (!outcome.Ok)
            {
                return OperationResult<T>.Failure(outcome.Error!, outcome.Code);
            }

            var (next, result) = outcome.Value;
            if (ReferenceEquals(next, _current))
            {
                return OperationResult<T>.Success(result); // nada mudou: não regrava o arquivo
            }

            try
            {
                _store.Save(next);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"Monitoramento: falha ao gravar a configuração: {ex.GetType().Name}");
                return OperationResult<T>.Failure("Não foi possível gravar a configuração. Verifique se o arquivo não está em uso e tente de novo.");
            }

            Volatile.Write(ref _current, next);
            Interlocked.Increment(ref _generation); // depois da troca: quem lê a geração nova lê a config nova
            return OperationResult<T>.Success(result);
        }
    }

    /// <summary>Somente leitura: tenta reler o arquivo; se der certo, passa a valer o que está no disco.</summary>
    private bool TryReload()
    {
        var loaded = _store.Load();
        if (_store.LastLoadFailed)
        {
            return false;
        }

        Volatile.Write(ref _current, loaded);
        Interlocked.Increment(ref _generation);
        _loadError = _store.LastLoadError;
        _readOnly = false;
        return true;
    }
}
