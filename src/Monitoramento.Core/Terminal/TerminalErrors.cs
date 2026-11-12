namespace Monitoramento.Core.Terminal;

/// <summary>Mensagens (pt-BR) e códigos de erro dos terminais que a UI precisa reconhecer.</summary>
internal static class TerminalErrors
{
    /// <summary>Código do erro de repositório cuja config pode executar programas (a UI pede confirmação).</summary>
    public const string UnsafeRepoCode = "unsafeRepo";

    public const string UnsafeRepoPrefix = "A configuração git deste repositório pode executar programas (";
    public const string UnsafeRepoSuffix = "). Abrir o terminal mesmo assim?";

    public const string NotAWorktreeOfSavedRepo = "A pasta não é um worktree do repositório deste processo salvo.";
    public const string SavedWithoutRepo = "O processo salvo não está dentro de um repositório git (defina a pasta de trabalho dele).";
}
