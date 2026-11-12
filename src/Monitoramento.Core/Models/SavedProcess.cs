namespace Monitoramento.Core.Models;

/// <summary>Como um processo salvo abre: "terminal" (aba embutida, padrão), "window" (console separado) ou "hidden".</summary>
public static class LaunchModes
{
    public const string Terminal = "terminal";
    public const string Window = "window";
    public const string Hidden = "hidden";

    /// <summary>Valor conhecido (sem diferenciar maiúsculas); ausente ou desconhecido vira <see cref="Terminal"/>.</summary>
    public static string Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        Window => Window,
        Hidden => Hidden,
        _ => Terminal,
    };
}

/// <summary>Comando salvo pelo usuário para iniciar/parar pelo app.</summary>
public record SavedProcess
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    /// <summary><see cref="LaunchModes"/>; config antiga (sem o campo, com "showWindow") passa a "terminal".</summary>
    public string LaunchMode { get; init; } = LaunchModes.Terminal;

    public string? Notes { get; init; }
}

/// <summary>Processo salvo acrescido do estado em execução.</summary>
public sealed record SavedProcessState : SavedProcess
{
    public SavedProcessState()
    {
    }

    public SavedProcessState(SavedProcess saved)
        : base(saved)
    {
    }

    public bool Running { get; init; }
    public IReadOnlyList<int> Pids { get; init; } = [];
    public long MemoryBytes { get; init; }
    public double CpuPercent { get; init; }
    public string? StartedAt { get; init; }
    public GitInfo? Git { get; init; }
    public string? LastError { get; init; }

    /// <summary>Terminal embutido deste salvo (modo "terminal"), ou null.</summary>
    public string? TerminalId { get; init; }
}
