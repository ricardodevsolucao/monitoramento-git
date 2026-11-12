using System.Text.Json;
using Monitoramento.Core.Bridge;
using Monitoramento.Core.Models;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Bridge;

/// <summary>Mensagens git da ponte (gitDetails e ações) com repositório real visto num processo do snapshot.</summary>
public sealed class BridgeGitTests : IDisposable
{
    private readonly EngineHarness _h = new();
    private readonly GitTestRepo _repo = GitTestRepo.CreateEmpty();

    public BridgeGitTests()
    {
        _repo.CommitFile("a.txt", "a", "inicial");
        _repo.Git("tag", "v1");
        _repo.WriteFile("u.txt", "u");
    }

    public void Dispose()
    {
        _h.Dispose();
        _repo.Dispose();
    }

    private BridgeRouter Router => new(_h.Engine);

    private void SeeRepoInSnapshot()
    {
        _h.Git.ByDirectory[_repo.Path] = new GitInfo(_repo.Path, "repo", "main", null, null, null);
        _h.AddProcess(100, "node.exe", 1000, "node app.js", _repo.Path);
        _h.Engine.Sample();
    }

    private async Task<JsonElement> SendAsync(string type, object payload)
    {
        var message = JsonSerializer.Serialize(new { id = "g1", type, payload });
        return JsonDocument.Parse(await Router.HandleMessageAsync(message)).RootElement.Clone();
    }

    [Fact]
    public async Task GitDetails_ForRepoSeenInSnapshot_ReturnsContractJson()
    {
        SeeRepoInSnapshot();

        var reply = await SendAsync("gitDetails", new { repoRoot = _repo.Path, maxCommits = 10 });

        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        var details = reply.GetProperty("data").GetProperty("details");
        Assert.Equal(_repo.Path, details.GetProperty("repo").GetProperty("repoRoot").GetString());
        Assert.Equal("main", details.GetProperty("head").GetProperty("branch").GetString());
        var change = details.GetProperty("changes")[0];
        Assert.Equal("u.txt", change.GetProperty("path").GetString());
        Assert.Equal("?", change.GetProperty("worktree").GetString());
        Assert.Equal(JsonValueKind.Null, change.GetProperty("index").ValueKind);
        var commit = details.GetProperty("commits")[0];
        Assert.Equal("inicial", commit.GetProperty("subject").GetString());
        var kinds = commit.GetProperty("refs").EnumerateArray().Select(r => r.GetProperty("kind").GetString()).ToList();
        Assert.Contains("head", kinds);
        Assert.Contains("tag", kinds);
        Assert.Equal(1, details.GetProperty("graph").GetProperty("lanes").GetInt32());
        Assert.False(details.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, details.GetProperty("error").ValueKind);
        Assert.Equal(1, details.GetProperty("untrackedCount").GetInt32());
        Assert.False(details.GetProperty("merging").GetBoolean());
        Assert.Equal(0, details.GetProperty("remotes").GetArrayLength());
    }

    [Fact]
    public async Task GitDetails_RepoFoundInSubfolderOfProcessWorkspace_IsAccepted()
    {
        var workspace = Path.GetDirectoryName(_repo.Path)!;
        _h.Git.ChildrenByDirectory[workspace] = [new GitInfo(_repo.Path, "repo", "main", null, null, null)];
        _h.AddProcess(100, "node.exe", 1000, "node app.js", workspace);
        _h.Engine.Sample();

        var reply = await SendAsync("gitDetails", new { repoRoot = _repo.Path });

        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        Assert.Equal("inicial", reply.GetProperty("data").GetProperty("details").GetProperty("commits")[0].GetProperty("subject").GetString());
    }

    [Fact]
    public async Task GitDetails_UnknownRepo_IsRefusedInPortuguese()
    {
        var reply = await SendAsync("gitDetails", new { repoRoot = _repo.Path });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("Repositório desconhecido; abra-o a partir de um processo.", reply.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GitDetails_InvalidMaxCommitsType_IsRejected()
    {
        SeeRepoInSnapshot();

        var reply = await SendAsync("gitDetails", new { repoRoot = _repo.Path, maxCommits = "muitos" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task GitStage_ThenCommit_ReturnsRefreshedDetails()
    {
        SeeRepoInSnapshot();

        var staged = await SendAsync("gitStage", new { repoRoot = _repo.Path, paths = new[] { "u.txt" } });
        Assert.True(staged.GetProperty("ok").GetBoolean(), staged.ToString());
        Assert.Equal(1, staged.GetProperty("data").GetProperty("details").GetProperty("stagedCount").GetInt32());

        var committed = await SendAsync("gitCommit", new { repoRoot = _repo.Path, message = "segundo", all = false, amend = false, push = false });
        Assert.True(committed.GetProperty("ok").GetBoolean(), committed.ToString());
        var details = committed.GetProperty("data").GetProperty("details");
        Assert.Equal("segundo", details.GetProperty("commits")[0].GetProperty("subject").GetString());
        Assert.Equal(0, details.GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public async Task GitAction_UnknownRepo_IsRefused()
    {
        var reply = await SendAsync("gitFetch", new { repoRoot = _repo.Path });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("Repositório desconhecido; abra-o a partir de um processo.", reply.GetProperty("error").GetString());
    }
}
