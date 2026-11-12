namespace Monitoramento.Core.Git;

/// <summary>
/// Decide se é seguro rodar o git num repositório qualquer encontrado na máquina. A config local do repositório é dado
/// não confiável: <c>core.fsmonitor</c>, drivers <c>filter.*</c>, <c>include</c>/<c>includeIf</c>, <c>gpg.*</c> etc.
/// podem fazer o git executar comandos. A config é interpretada pelo parser do próprio git
/// (<see cref="GitConfigReader"/>, que não executa nada), então qualquer sintaxe que o git aceite é vista igual a ele;
/// arquivo inválido ou ilegível → não roda.
/// </summary>
internal static class GitRepoSafety
{
    public const string FsmonitorNote = "Status não lido: a config do repositório define core.fsmonitor.";
    public const string FilterNote = "Status não lido: a config do repositório define um filtro (filter).";
    public const string IncludeNote = "Status não lido: a config do repositório usa include/includeIf.";
    public const string UnreadableNote = "Status não lido: não foi possível ler a config do repositório.";
    public const string InvalidConfigNote = "Status não lido: a config do repositório é inválida ou não pôde ser lida.";
    public const string NonLocalNote = "Status não lido: o repositório aponta para uma pasta que não é local.";

    /// <summary>Prefixos das mensagens do bloqueio estendido (visão Git).</summary>
    public const string LogBlockedPrefix = "Histórico não lido: ";
    public const string StatusBlockedPrefix = "Status não lido: ";

    /// <returns>Motivo (em português) para não rodar o git status, ou null se for seguro.</returns>
    public static Task<string?> CheckAsync(GitRepoLocation location, GitConfigReader? reader = null) =>
        ScanAsync(location, reader ?? GitConfigReader.Default, StatusReason);

    /// <summary>
    /// Checagem estendida para o log/for-each-ref da visão Git: além do que é passado por <c>-c</c> na linha de
    /// comando, recusa a config local que manda o git executar programas ao listar commits.
    /// </summary>
    /// <returns>Motivo (ex.: "a config do repositório define gpg."), ou null se for seguro.</returns>
    public static Task<string?> CheckLogAsync(GitRepoLocation location, GitConfigReader? reader = null) =>
        ScanAsync(location, reader ?? GitConfigReader.Default, LogReason);

    /// <summary>Checagem do status sobre um texto de config (grava num arquivo temporário e usa o parser do git).</summary>
    public static Task<string?> InspectConfigAsync(string text) => InspectTextAsync(text, StatusReason);

    /// <summary>Checagem estendida (log) sobre um texto de config.</summary>
    public static Task<string?> InspectLogConfigAsync(string text) => InspectTextAsync(text, LogReason);

    /// <summary><c>filter.*</c>, <c>include.*</c>, <c>includeIf.*</c> e <c>core.fsmonitor</c> que não seja falso.</summary>
    private static string? StatusReason(IEnumerable<GitConfigEntry> entries)
    {
        foreach (var (key, value) in entries)
        {
            if (key.StartsWith("filter.", StringComparison.Ordinal))
            {
                return FilterNote;
            }

            if (IsInclude(key))
            {
                return IncludeNote;
            }

            if (key == "core.fsmonitor" && !IsFalse(value))
            {
                return FsmonitorNote;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>gpg.*</c>, <c>include*</c>, <c>log.showSignature</c> (não falso), <c>core.alternateRefsCommand</c>,
    /// <c>diff.*.textconv</c>, <c>diff.*.command</c> e <c>diff.external</c>.
    /// </summary>
    private static string? LogReason(IEnumerable<GitConfigEntry> entries)
    {
        foreach (var (key, value) in entries)
        {
            string? reason = null;
            if (key.StartsWith("gpg.", StringComparison.Ordinal))
            {
                reason = "a config do repositório define gpg.";
            }
            else if (IsInclude(key))
            {
                reason = "a config do repositório usa include/includeIf.";
            }
            else if (key == "log.showsignature" && !IsFalse(value))
            {
                reason = "a config do repositório define log.showSignature.";
            }
            else if (key == "core.alternaterefscommand")
            {
                reason = "a config do repositório define core.alternateRefsCommand.";
            }
            else if (key.StartsWith("diff.", StringComparison.Ordinal)
                && (key == "diff.external" || key.EndsWith(".textconv", StringComparison.Ordinal) || key.EndsWith(".command", StringComparison.Ordinal)))
            {
                reason = "a config do repositório define um comando de diff (diff.*).";
            }

            if (reason is not null)
            {
                return reason;
            }
        }

        return null;
    }

    private static bool IsInclude(string key) =>
        key.StartsWith("include.", StringComparison.Ordinal) || key.StartsWith("includeif.", StringComparison.Ordinal);

    /// <summary>Booleano falso do git (false/no/off/0 ou vazio). Chave sem valor vale true.</summary>
    private static bool IsFalse(string? value) =>
        value is not null && value.Trim().ToLowerInvariant() is "" or "false" or "no" or "off" or "0";

    private static async Task<string?> ScanAsync(GitRepoLocation location, GitConfigReader reader, Func<IEnumerable<GitConfigEntry>, string?> reason)
    {
        try
        {
            var commonDir = GitRepositoryLocator.CommonDir(location.GitDir);
            if (commonDir is null)
            {
                return NonLocalNote;
            }

            string[] configs =
            [
                Path.Combine(commonDir, "config"),
                Path.Combine(commonDir, "config.worktree"),
                Path.Combine(location.GitDir, "config"),
                Path.Combine(location.GitDir, "config.worktree"),
            ];
            foreach (var file in configs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var entries = await reader.ReadAsync(file).ConfigureAwait(false);
                if (entries is null)
                {
                    return InvalidConfigNote;
                }

                if (reason(entries) is { } note)
                {
                    return note;
                }
            }

            // O .gitattributes só aponta para drivers; quem define o comando é a config. A local já foi checada
            // acima, e a global/sistema é do próprio usuário (ex.: git-lfs) — por isso não é bloqueado.
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return UnreadableNote;
        }
    }

    private static async Task<string?> InspectTextAsync(string text, Func<IEnumerable<GitConfigEntry>, string?> reason)
    {
        var file = Path.Combine(GitConfigReader.NeutralDirectory, "inspecao-" + Guid.NewGuid().ToString("N") + ".cfg");
        await File.WriteAllTextAsync(file, text).ConfigureAwait(false);
        try
        {
            var entries = await new GitConfigReader(GitExecutable.Find()).ReadAsync(file).ConfigureAwait(false);
            return entries is null ? InvalidConfigNote : reason(entries);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
