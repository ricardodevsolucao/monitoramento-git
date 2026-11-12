using Monitoramento.Core.Git;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Tests.Git;

/// <summary>Saneamento do workspace git vindo da UI (setGitWorkspace).</summary>
public sealed class GitWorkspaceRulesTests
{
    private const string Folder = @"C:\proj\gsintegra";
    private const string App = @"C:\proj\gsintegra\app";
    private const string Api = @"C:\proj\gsintegra\api";

    private static readonly HashSet<string> KnownRepos = new(StringComparer.OrdinalIgnoreCase) { App, Api };

    private static GitWorkspace Sanitize(GitWorkspace? input, params string[] projectIds) =>
        GitWorkspaceRules.Sanitize(input, Context(KnownRepos.Contains, projectIds));

    private static WorkspaceContext Context(Func<string, bool> isKnown, string[]? projectIds = null, params string[] saved) =>
        new(isKnown, new HashSet<string>(projectIds ?? [], StringComparer.Ordinal), new HashSet<string>(saved, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Null_ReturnsDefaults()
    {
        var ws = Sanitize(null);

        Assert.False(ws.Docked);
        Assert.Equal(42, ws.DockWidthPct);
        Assert.Empty(ws.Tabs);
        Assert.Null(ws.Active);
    }

    [Theory]
    [InlineData(10, 25)]
    [InlineData(99, 70)]
    [InlineData(50.5, 50.5)]
    public void DockWidth_IsClamped(double input, double expected)
    {
        Assert.Equal(expected, Sanitize(new GitWorkspace { DockWidthPct = input }).DockWidthPct);
    }

    [Fact]
    public void UnknownRepos_AreDropped_AndTabWithoutReposIsRemoved()
    {
        var ws = Sanitize(new GitWorkspace
        {
            Docked = true,
            Tabs =
            [
                new GitTab { Folder = Folder, Name = "gsintegra", Repos = [App, @"C:\Windows", @"\\servidor\share\repo", Api, App.ToUpperInvariant()] },
                new GitTab { Folder = @"C:\estranho", Name = "x", Repos = [@"C:\Windows"] },
            ],
            Active = Folder,
        });

        Assert.True(ws.Docked);
        var tab = Assert.Single(ws.Tabs);
        Assert.Equal([App, Api], tab.Repos);
        Assert.Equal(Folder, ws.Active);
    }

    [Fact]
    public void InvalidProjectId_BecomesNull_AndValidIsKept()
    {
        var ws = Sanitize(new GitWorkspace
        {
            Tabs =
            [
                new GitTab { Folder = Folder, Name = "a", Repos = [App], ProjectId = "nao-existe" },
                new GitTab { Folder = App, Name = "b", Repos = [App], ProjectId = "p1" },
            ],
        }, "p1");

        Assert.Null(ws.Tabs[0].ProjectId);
        Assert.Equal("p1", ws.Tabs[1].ProjectId);
    }

    [Fact]
    public void InvalidActiveRepo_BecomesFirstRepo_AndValidIsKeptNormalized()
    {
        var ws = Sanitize(new GitWorkspace
        {
            Tabs =
            [
                new GitTab { Folder = Folder, Name = "a", Repos = [App, Api], ActiveRepo = @"C:\Windows" },
                new GitTab { Folder = App, Name = "b", Repos = [App, Api], ActiveRepo = Api + "\\" },
            ],
        });

        Assert.Equal(App, ws.Tabs[0].ActiveRepo);
        Assert.Equal(Api, ws.Tabs[1].ActiveRepo);
    }

    [Fact]
    public void InvalidActive_BecomesFirstTab_OrNullWithoutTabs()
    {
        var withTabs = Sanitize(new GitWorkspace
        {
            Tabs = [new GitTab { Folder = Folder, Name = "a", Repos = [App] }],
            Active = @"C:\outra",
        });
        var withoutTabs = Sanitize(new GitWorkspace { Active = Folder });

        Assert.Equal(Folder, withTabs.Active);
        Assert.Null(withoutTabs.Active);
    }

    [Fact]
    public void Limits_TwelveTabs_TwelveReposPerTab_AndDuplicateFolders()
    {
        static List<string> Repos(int tab) => Enumerable.Range(0, 20).Select(i => $@"C:\r{tab:00}\repo{i:00}").ToList();
        var known = new HashSet<string>(Enumerable.Range(0, 15).SelectMany(Repos), StringComparer.OrdinalIgnoreCase);
        var tabs = Enumerable.Range(0, 20)
            .Select(i => new GitTab { Folder = $@"C:\r{i % 15:00}", Name = $"t{i}", Repos = Repos(i % 15) })
            .ToList();

        var ws = GitWorkspaceRules.Sanitize(new GitWorkspace { Tabs = tabs }, Context(known.Contains));

        Assert.Equal(GitWorkspace.MaxTabs, ws.Tabs.Count);
        Assert.All(ws.Tabs, t => Assert.Equal(GitWorkspace.MaxReposPerTab, t.Repos.Count));
        Assert.Equal(ws.Tabs.Count, ws.Tabs.Select(t => t.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void TabFolder_NonLocal_RemovesTab_AndNameIsTrimmedOrDefaulted()
    {
        var ws = Sanitize(new GitWorkspace
        {
            Tabs =
            [
                new GitTab { Folder = @"\\servidor\share", Name = "rede", Repos = [App] },
                new GitTab { Folder = Folder + "\\", Name = "   ", Repos = [App] },
                new GitTab { Folder = App, Name = "  " + new string('n', 100) + "  ", Repos = [App] },
            ],
        });

        Assert.Equal(2, ws.Tabs.Count);
        Assert.Equal(Folder, ws.Tabs[0].Folder);
        Assert.Equal("gsintegra", ws.Tabs[0].Name);
        Assert.Equal(GitProject.MaxNameLength, ws.Tabs[1].Name.Length);
    }

    [Fact]
    public void NullCollectionsFromJson_AreTolerated()
    {
        var ws = Sanitize(new GitWorkspace
        {
            Tabs = [null!, new GitTab { Folder = Folder, Name = null!, Repos = null! }, new GitTab { Folder = Folder, Name = "x", Repos = [null!, App] }],
        });

        var tab = Assert.Single(ws.Tabs);
        Assert.Equal([App], tab.Repos);
    }

    [Theory]
    [InlineData(@"C:\proj\gsintegra", @"C:\proj\gsintegra")] // mãe direta: fica
    [InlineData(@"C:\proj\gsintegra\app", @"C:\proj\gsintegra\app")] // raiz de repo da aba: fica
    [InlineData(@"C:\proj", @"C:\proj\gsintegra\app")] // avó: vira a raiz do primeiro repo
    [InlineData(@"C:\outra\pasta", @"C:\proj\gsintegra\app")] // sem relação: idem
    [InlineData(@"C:\", @"C:\proj\gsintegra\app")] // raiz da unidade: idem
    public void TabFolder_MustBeRootOrDirectParentOfARepo(string folder, string expected)
    {
        var ws = Sanitize(new GitWorkspace { Tabs = [new GitTab { Folder = folder, Name = "x", Repos = [App, Api] }] });

        Assert.Equal(expected, Assert.Single(ws.Tabs).Folder);
    }

    [Fact]
    public void TabFolder_Broad_BecomesFirstRepoRoot()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var repo = Path.Combine(profile, "repo-direto");
        var known = new HashSet<string>([repo], StringComparer.OrdinalIgnoreCase);

        var ws = GitWorkspaceRules.Sanitize(
            new GitWorkspace { Tabs = [new GitTab { Folder = profile, Name = "x", Repos = [repo] }] },
            Context(known.Contains));

        Assert.Equal(repo, Assert.Single(ws.Tabs).Folder);
    }

    [Fact]
    public void ReposAlreadySavedInTabs_AreKept_EvenIfNotKnownNow()
    {
        var absentDrive = Enumerable.Range('D', 23).Select(c => (char)c).First(c => !Directory.Exists($@"{c}:\"));
        var pendrive = $@"{absentDrive}:\projetos\repo";
        const string deleted = @"C:\proj\gsintegra\apagado";

        var ws = GitWorkspaceRules.Sanitize(
            new GitWorkspace
            {
                Tabs =
                [
                    new GitTab { Folder = Folder, Name = "g", Repos = [App, deleted, @"C:\proj\gsintegra\nunca-salvo"] },
                    new GitTab { Folder = pendrive, Name = "p", Repos = [pendrive] },
                ],
            },
            Context(KnownRepos.Contains, null, Folder, App, deleted, pendrive));

        Assert.Equal(2, ws.Tabs.Count);
        Assert.Equal([App, deleted], ws.Tabs[0].Repos);
        Assert.Equal(pendrive, ws.Tabs[1].Folder);
        Assert.Equal([pendrive], ws.Tabs[1].Repos);
    }

    [Fact]
    public void LowercaseDrive_IsNormalizedToUppercase_AndDeduplicated()
    {
        var ws = Sanitize(new GitWorkspace
        {
            Tabs = [new GitTab { Folder = "c" + Folder[1..], Name = "g", Repos = ["c" + App[1..], App, "c" + Api[1..]], ActiveRepo = "c" + Api[1..] }],
            Active = "c" + Folder[1..],
        });

        var tab = Assert.Single(ws.Tabs);
        Assert.Equal(Folder, tab.Folder);
        Assert.Equal([App, Api], tab.Repos);
        Assert.Equal(Api, tab.ActiveRepo);
        Assert.Equal(Folder, ws.Active);
    }
}
