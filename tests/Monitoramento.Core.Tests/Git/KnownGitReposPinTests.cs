using Monitoramento.Core.Git;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Git;

/// <summary>Repositórios fixados (projetos salvos e abas do workspace) não expiram como os vistos em processos.</summary>
public sealed class KnownGitReposPinTests
{
    private const string App = @"C:\proj\gsintegra\app";
    private const string Api = @"C:\proj\gsintegra\api";

    [Fact]
    public void Pinned_DoesNotExpire()
    {
        var time = new ManualTimeProvider();
        var known = new KnownGitRepos(time);

        known.SetPinned([App]);
        time.Advance(KnownGitRepos.Expiry + TimeSpan.FromHours(5));

        Assert.True(known.Contains(App));
    }

    [Fact]
    public void Registered_StillExpires()
    {
        var time = new ManualTimeProvider();
        var known = new KnownGitRepos(time);

        known.Register(Api);
        time.Advance(KnownGitRepos.Expiry + TimeSpan.FromMinutes(1));

        Assert.False(known.Contains(Api));
    }

    [Fact]
    public void SetPinned_ReplacesPreviousSet_IgnoringCase()
    {
        var known = new KnownGitRepos(new ManualTimeProvider());

        known.SetPinned([App, Api]);
        known.SetPinned([Api.ToUpperInvariant()]);

        Assert.False(known.Contains(App));
        Assert.True(known.Contains(Api));
    }

    [Fact]
    public void Register_HasStrictCap_DroppingTheOldestWhenFullOfFreshEntries()
    {
        var time = new ManualTimeProvider();
        var known = new KnownGitRepos(time);
        for (var i = 0; i < KnownGitRepos.MaxEntries + 10; i++)
        {
            known.Register($@"C:\r\repo{i:0000}");
            time.Advance(TimeSpan.FromMilliseconds(10)); // todos ainda frescos (bem menos que 30 min)
        }

        Assert.Equal(KnownGitRepos.MaxEntries, known.SeenCount);
        Assert.False(known.Contains(@"C:\r\repo0000"));
        Assert.True(known.Contains($@"C:\r\repo{KnownGitRepos.MaxEntries + 9:0000}"));
    }

    [Fact]
    public void Normalize_UppercasesDriveLetter()
    {
        Assert.Equal(App, KnownGitRepos.Normalize("c" + App[1..] + "\\"));
    }

    [Fact]
    public void IsParentOfKnown_SeesPinnedAndFreshRegistered_ButNotExpired()
    {
        var time = new ManualTimeProvider();
        var known = new KnownGitRepos(time);
        known.Register(@"C:\outro\repo");
        known.SetPinned([App]);

        Assert.True(known.IsParentOfKnown(@"C:\proj\gsintegra"));
        Assert.True(known.IsParentOfKnown(@"C:\outro"));
        Assert.False(known.IsParentOfKnown(@"C:\proj")); // só a mãe direta

        time.Advance(KnownGitRepos.Expiry + TimeSpan.FromMinutes(1));

        Assert.False(known.IsParentOfKnown(@"C:\outro"));
        Assert.True(known.IsParentOfKnown(@"C:\proj\gsintegra"));
    }
}
