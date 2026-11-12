using Monitoramento.Core.Git;

namespace Monitoramento.Core.Tests.Git;

/// <summary>Pastas amplas demais para virar projeto/aba (raiz da unidade, pastas do usuário e do sistema).</summary>
public sealed class BroadFoldersTests
{
    public static TheoryData<string> Broad()
    {
        var data = new TheoryData<string>
        {
            @"C:\",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        if (Environment.GetEnvironmentVariable("OneDrive") is { Length: > 0 } oneDrive)
        {
            data.Add(oneDrive);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Broad))]
    public void IsBroad_TrueForDriveRootUserAndSystemFolders(string folder)
    {
        Assert.True(BroadFolders.IsBroad(KnownGitRepos.Normalize(folder)!));
    }

    [Fact]
    public void IsBroad_IgnoresCaseAndTrailingSeparator()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.True(BroadFolders.IsBroad(KnownGitRepos.Normalize(profile.ToLowerInvariant() + "\\")!));
    }

    [Theory]
    [InlineData(@"C:\src\gsintegra")]
    [InlineData(@"C:\Windows\System32")]
    public void IsBroad_FalseForOrdinaryAndNestedFolders(string folder)
    {
        Assert.False(BroadFolders.IsBroad(folder));
    }

    [Fact]
    public void IsBroad_FalseForProjectFolderInsideDocuments()
    {
        var projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Projetos");

        Assert.False(BroadFolders.IsBroad(KnownGitRepos.Normalize(projects)!));
    }
}
