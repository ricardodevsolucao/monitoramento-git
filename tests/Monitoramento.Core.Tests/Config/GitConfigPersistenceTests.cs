using System.Text.Json;
using Monitoramento.Core.Config;
using Monitoramento.Core.Models;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Config;

/// <summary>gitProjects e gitWorkspace no config.json: compatibilidade com config antiga, ida e volta e limites.</summary>
public sealed class GitConfigPersistenceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string ConfigPath => _temp.Combine("config.json");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void OldConfigWithoutGitFields_LoadsWithDefaults()
    {
        File.WriteAllText(ConfigPath, """{ "favorites": ["app:code.exe"], "saved": [], "settings": { "refreshIntervalMs": 1000, "minimizeToTray": false, "theme": "light" } }""");
        var store = new ConfigStore(ConfigPath);

        var config = store.Load();

        Assert.Null(store.LastLoadError);
        Assert.Equal(["app:code.exe"], config.Favorites);
        Assert.Empty(config.GitProjects);
        Assert.False(config.GitWorkspace.Docked);
        Assert.Equal(42, config.GitWorkspace.DockWidthPct);
        Assert.Empty(config.GitWorkspace.Tabs);
    }

    [Fact]
    public void ExplicitNulls_LoadWithDefaults()
    {
        File.WriteAllText(ConfigPath, """{ "gitProjects": null, "gitWorkspace": null }""");

        var config = new ConfigStore(ConfigPath).Load();

        Assert.Empty(config.GitProjects);
        Assert.NotNull(config.GitWorkspace);
        Assert.Empty(config.GitWorkspace.Tabs);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsProjectsAndWorkspace()
    {
        var store = new ConfigStore(ConfigPath);
        var config = new AppConfig
        {
            GitProjects = [new GitProject { Id = "p1", Name = "Integra", Folder = @"C:\src\gsintegra", Repos = [@"C:\src\gsintegra\api", @"C:\src\gsintegra\app"] }],
            GitWorkspace = new GitWorkspace
            {
                Docked = true,
                DockWidthPct = 55.5,
                Tabs = [new GitTab { Folder = @"C:\src\gsintegra", Name = "Integra", Repos = [@"C:\src\gsintegra\app"], ProjectId = "p1", ActiveRepo = @"C:\src\gsintegra\app" }],
                Active = @"C:\src\gsintegra",
            },
        };

        store.Save(config);
        var loaded = store.Load();

        var project = Assert.Single(loaded.GitProjects);
        Assert.Equal("Integra", project.Name);
        Assert.Equal(config.GitProjects[0].Repos, project.Repos);
        Assert.True(loaded.GitWorkspace.Docked);
        Assert.Equal(55.5, loaded.GitWorkspace.DockWidthPct);
        var tab = Assert.Single(loaded.GitWorkspace.Tabs);
        Assert.Equal("p1", tab.ProjectId);
        Assert.Equal(@"C:\src\gsintegra\app", tab.ActiveRepo);
        Assert.Equal(@"C:\src\gsintegra", loaded.GitWorkspace.Active);
        Assert.Contains("\"dockWidthPct\"", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_DropsInvalidProjects_DuplicatesAndAppliesLimits()
    {
        var projects = new List<object>
        {
            new { id = "", name = "sem id", folder = @"C:\a", repos = Array.Empty<string>() },
            new { id = "x", name = "sem pasta", folder = "", repos = Array.Empty<string>() },
            new { id = "dup", name = "1", folder = @"C:\b", repos = Array.Empty<string>() },
            new { id = "dup", name = "2", folder = @"C:\c", repos = Array.Empty<string>() },
            new { id = "outra", name = "mesma pasta", folder = @"C:\B", repos = Array.Empty<string>() },
        };
        projects.AddRange(Enumerable.Range(0, 60).Select(i => new { id = $"p{i}", name = "", folder = $@"C:\p\{i}", repos = Enumerable.Range(0, 20).Select(r => $@"C:\p\{i}\r{r}").ToArray() }));
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new
        {
            gitProjects = projects,
            gitWorkspace = new { dockWidthPct = 3, tabs = Enumerable.Range(0, 20).Select(i => new { folder = $@"C:\t\{i}", name = "t", repos = new[] { $@"C:\t\{i}\r" } }) },
        }));
        var store = new ConfigStore(ConfigPath);

        var config = store.Load();

        Assert.Equal(GitProject.MaxProjects, config.GitProjects.Count);
        Assert.True(Guid.TryParse(config.GitProjects[0].Id, out _)); // id ausente → guid novo, não descarta
        Assert.Equal("sem id", config.GitProjects[0].Name);
        Assert.Equal("dup", config.GitProjects[1].Id);
        Assert.Equal("p0", config.GitProjects[2].Id);
        Assert.Equal("0", config.GitProjects[2].Name); // nome vazio → nome da pasta
        Assert.All(config.GitProjects, p => Assert.True(p.Repos.Count <= GitWorkspace.MaxReposPerTab));
        Assert.Equal(25, config.GitWorkspace.DockWidthPct);
        Assert.Equal(GitWorkspace.MaxTabs, config.GitWorkspace.Tabs.Count);
        Assert.NotNull(store.LastLoadError);
        Assert.Contains("50 projetos", store.LastLoadError, StringComparison.Ordinal);
        Assert.Contains("12 abas", store.LastLoadError, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_WithinLimits_HasNoWarning_AndUppercasesDrive()
    {
        const string lower = @"c:\src\a";
        const string upper = @"C:\src\a";
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new
        {
            gitProjects = new[] { new { id = "p1", name = "a", folder = lower + "\\", repos = new[] { lower } } },
            gitWorkspace = new { tabs = new[] { new { folder = lower, name = "a", repos = new[] { lower }, activeRepo = lower } }, active = lower },
        }));
        var store = new ConfigStore(ConfigPath);

        var config = store.Load();

        Assert.Null(store.LastLoadError);
        Assert.Equal(upper, config.GitProjects[0].Folder);
        Assert.Equal([upper], config.GitProjects[0].Repos);
        Assert.Equal(upper, config.GitWorkspace.Tabs[0].Folder);
        Assert.Equal(upper, config.GitWorkspace.Tabs[0].ActiveRepo);
        Assert.Equal(upper, config.GitWorkspace.Active);
    }
}
