namespace Monitoramento.Core.Models;

public sealed record Settings
{
    public const int DefaultRefreshIntervalMs = 2000;
    public const int MinRefreshIntervalMs = 500;
    public const int MaxRefreshIntervalMs = 60_000;

    public int RefreshIntervalMs { get; init; } = DefaultRefreshIntervalMs;
    public bool MinimizeToTray { get; init; } = true;

    /// <summary>"system", "light" ou "dark".</summary>
    public string Theme { get; init; } = "system";
}

public sealed record AppConfig
{
    public IReadOnlyList<string> Favorites { get; init; } = [];
    public IReadOnlyList<SavedProcess> Saved { get; init; } = [];
    public Settings Settings { get; init; } = new();
    public IReadOnlyList<GitProject> GitProjects { get; init; } = [];
    public GitWorkspace GitWorkspace { get; init; } = new();
}
