namespace Monitoramento.Core.Models;

/// <summary>
/// Projeto git salvo: uma pasta que agrupa 1+ repositórios (ex.: gsintegra → app e api). <see cref="Repos"/> é
/// sempre calculado pelo Core a partir de <see cref="Folder"/> (nunca vem da UI).
/// </summary>
public sealed record GitProject
{
    public const int MaxNameLength = 80;
    public const int MaxProjects = 50;

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Folder { get; init; } = "";

    /// <summary>Raízes dos repositórios, ordenadas por nome.</summary>
    public IReadOnlyList<string> Repos { get; init; } = [];
}

/// <summary>Aba aberta no workspace git.</summary>
public sealed record GitTab
{
    public string Folder { get; init; } = "";
    public string Name { get; init; } = "";
    public IReadOnlyList<string> Repos { get; init; } = [];
    public string? ProjectId { get; init; }
    public string? ActiveRepo { get; init; }

    /// <summary>Aba de worktree: raiz do repositório principal (folder = caminho do worktree, repos = [folder]).</summary>
    public string? WorktreeOf { get; init; }
}

/// <summary>Estado persistido do workspace git (abas abertas e painel fixado à direita).</summary>
public sealed record GitWorkspace
{
    public const double MinDockWidthPct = 25;
    public const double MaxDockWidthPct = 70;
    public const double DefaultDockWidthPct = 42;
    public const int MaxTabs = 12;
    public const int MaxReposPerTab = 12;

    public bool Docked { get; init; }
    public double DockWidthPct { get; init; } = DefaultDockWidthPct;
    public IReadOnlyList<GitTab> Tabs { get; init; } = [];

    /// <summary>Pasta (<see cref="GitTab.Folder"/>) da aba ativa, ou null.</summary>
    public string? Active { get; init; }
}
