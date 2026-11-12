using System.Text.Json;
using Monitoramento.Core.Actions;
using Monitoramento.Core.Bridge;
using Monitoramento.Core.Git;
using Monitoramento.Core.Icons;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Security;

/// <summary>S2: caminhos de rede/dispositivo vindos de dado não confiável são recusados em todos os pontos de uso.</summary>
public sealed class LocalPathUsageTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    public static TheoryData<string> DevicePrefixes => [@"\\?\", @"\\.\", "//?/", @"\\?\GLOBALROOT\??\"];

    [Theory]
    [MemberData(nameof(DevicePrefixes))]
    public void OpenFolder_RefusesDevicePaths(string prefix)
    {
        var starter = new FakeStarter();
        var actions = new ProcessActions(starter, new FakeKiller(), 999);

        Assert.False(actions.OpenFolder(prefix + _temp.Path).Ok);
        Assert.False(actions.OpenTerminal(prefix + _temp.Path).Ok);
        Assert.Empty(starter.Started);
    }

    [Theory]
    [InlineData(@"\\host\share")]
    [InlineData("//host/share")]
    public void OpenFolder_RefusesNetworkPaths(string path)
    {
        var starter = new FakeStarter();

        Assert.False(new ProcessActions(starter, new FakeKiller(), 999).OpenFolder(path).Ok);
        Assert.Empty(starter.Started);
    }

    [Fact]
    public void IconProvider_RefusesDevicePath()
    {
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");

        Assert.Null(new IconProvider().GetPng(@"\\?\" + notepad));
        Assert.Null(new IconProvider().GetPng("//?/" + notepad));
        Assert.NotNull(new IconProvider().GetPng(notepad));
    }

    [Fact]
    public void Locate_RefusesDeviceStartDirectory()
    {
        using var repo = GitTestRepo.CreateWithCommit();

        Assert.Null(GitRepositoryLocator.Locate(@"\\?\" + repo.Path));
        Assert.Null(GitRepositoryLocator.Locate(@"\\.\" + repo.Path));
        Assert.NotNull(GitRepositoryLocator.Locate(repo.Path));
    }

    [Theory]
    [InlineData(@"\\?\")]
    [InlineData(@"\\.\")]
    public void Locate_RefusesGitdirPointingToDevicePath(string prefix)
    {
        using var repo = GitTestRepo.CreateWithCommit();
        var worktree = Directory.CreateDirectory(_temp.Combine("wt")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + prefix + Path.Combine(repo.Path, ".git"));

        Assert.Null(GitRepositoryLocator.Locate(worktree));
    }

    [Theory]
    [InlineData(@"gitdir: \\127.0.0.1\c$\repo\.git")]
    [InlineData("gitdir: //127.0.0.1/c$/repo/.git")]
    public void Locate_RefusesGitdirPointingToUnc(string content)
    {
        var worktree = Directory.CreateDirectory(_temp.Combine("wt")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), content);

        Assert.Null(GitRepositoryLocator.Locate(worktree));
    }

    [Fact]
    public void CommandLinePaths_RefusesForwardSlashUncAndDevicePaths()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "//host/share/app.js", @"\\?\C:\repo\app.js" };

        Assert.Null(CommandLinePaths.FindDirectory("node //host/share/app.js", _ => false, files.Contains));
        Assert.Null(CommandLinePaths.FindDirectory(@"node \\?\C:\repo\app.js", _ => false, files.Contains));
    }

    [Fact]
    public async Task StartSaved_RefusesDeviceWorkingDirectory()
    {
        using var h = new EngineHarness();
        var router = new BridgeRouter(h.Engine);
        var save = JsonSerializer.Serialize(new { id = "1", type = "saveProcess", payload = new { saved = new { id = "", name = "x", command = "dir", workingDirectory = @"\\?\" + h.Temp.Path } } });
        var id = JsonDocument.Parse(await router.HandleMessageAsync(save)).RootElement.GetProperty("data").GetProperty("saved").GetProperty("id").GetString();

        var reply = JsonDocument.Parse(await router.HandleMessageAsync(JsonSerializer.Serialize(new { id = "2", type = "startSaved", payload = new { id } }))).RootElement;

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("local", reply.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Empty(h.Starter.Launched);
    }
}
