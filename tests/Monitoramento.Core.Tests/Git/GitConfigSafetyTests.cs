using System.Diagnostics;
using Monitoramento.Core.Git;
using Monitoramento.Core.Models;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Git;

/// <summary>
/// Revisão de segurança: a config local é lida pelo parser do próprio git (<c>git config --file … --list</c>), então
/// truques de sintaxe que o git aceita (dois cabeçalhos na mesma linha etc.) não escapam da checagem.
/// </summary>
public sealed class GitConfigSafetyTests
{
    private static string MarkerScript(GitTestRepo repo, string marker)
    {
        var script = repo.Temp.Combine("evil.sh");
        File.WriteAllText(script, $"#!/bin/sh\necho executou > \"{marker.Replace('\\', '/')}\"\ncat\n");
        return script.Replace('\\', '/');
    }

    private static void AppendLocalConfig(GitTestRepo repo, string text) =>
        File.AppendAllText(Path.Combine(repo.Path, ".git", "config"), text);

    private static async Task<GitInfo> SnapshotStatusAsync(string path)
    {
        var gitExe = GitExecutable.Find();
        Assert.NotNull(gitExe);
        using var cache = new GitStatusCache(gitExe, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5));
        var resolver = new GitInfoResolver(cache);
        GitInfo? info = null;
        for (var i = 0; i < 100; i++)
        {
            info = resolver.Resolve(path, null);
            if (info?.DirtyCount is not null || info?.StatusNote is not null)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.NotNull(info);
        return info;
    }

    [Fact]
    public async Task FilterHiddenBehindSecondHeaderOnSameLine_IsBlockedAndNeverRuns()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        var marker = repo.Temp.Combine("marker");
        AppendLocalConfig(repo, $"[foo][filter \"evil\"]\n\tclean = {MarkerScript(repo, marker)}\n");
        File.WriteAllText(Path.Combine(repo.Path, ".gitattributes"), "*.txt filter=evil\n");
        File.WriteAllText(Path.Combine(repo.Path, "readme.txt"), "olx"); // mesmo tamanho: o git precisa comparar o conteúdo (passa pelo filtro)
        File.SetLastWriteTimeUtc(Path.Combine(repo.Path, "readme.txt"), DateTime.UtcNow.AddMinutes(1));

        // controle positivo: o git do terminal executa o filtro durante o status
        GitTestRepo.RunGit(repo.Path, "status", "--porcelain");
        Assert.True(File.Exists(marker));
        File.Delete(marker);

        var info = await SnapshotStatusAsync(repo.Path);

        Assert.Equal(GitRepoSafety.FilterNote, info.StatusNote);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task IncludeHiddenBehindSecondHeader_IsBlocked()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        var marker = repo.Temp.Combine("marker");
        var included = repo.Temp.Combine("incluida.cfg");
        File.WriteAllText(included, $"[core]\n\tfsmonitor = {MarkerScript(repo, marker)}\n");
        AppendLocalConfig(repo, $"[x][include]\n\tpath = {included.Replace('\\', '/')}\n");

        var info = await SnapshotStatusAsync(repo.Path);

        Assert.Equal(GitRepoSafety.IncludeNote, info.StatusNote);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task GpgHiddenBehindSecondHeader_BlocksTheLogWithoutRunningGit()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        AppendLocalConfig(repo, "[x][gpg]\n\tprogram = c:/evil.exe\n[y][log]\n\tshowSignature = true\n");
        var time = new ManualTimeProvider();
        var known = new KnownGitRepos(time);
        var runner = CountingGitRunner.Real();
        known.Register(repo.Path);

        var d = (await new GitDetailsService(runner, known, time).GetAsync(repo.Path, null)).Value!;

        Assert.Equal(0, runner.Calls);
        Assert.StartsWith(GitRepoSafety.LogBlockedPrefix, d.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[x][filter \"e\"]\n\tclean = x\n", GitRepoSafety.FilterNote)]
    [InlineData("[x][include]\n\tpath = y\n", GitRepoSafety.IncludeNote)]
    [InlineData("[user]\n\tname = a\n[x] [core] fsmonitor = hook\n", GitRepoSafety.FsmonitorNote)]
    [InlineData("[filter \"com espaço \\\"aspas\\\"\"]\n\tsmudge = x\n", GitRepoSafety.FilterNote)]
    [InlineData("[includeIf \"gitdir:C:/a b/\"]\n\tpath = x\n", GitRepoSafety.IncludeNote)]
    [InlineData("[core]\n\tfsmonitor\n", GitRepoSafety.FsmonitorNote)]
    [InlineData("[core]\n\tbare = false\n[x\n", GitRepoSafety.InvalidConfigNote)]
    public async Task Inspect_UsesGitParser(string config, string expected)
    {
        Assert.Equal(expected, await GitRepoSafety.InspectConfigAsync(config));
    }

    [Theory]
    [InlineData("[core]\n\tfsmonitor = false\n\tautocrlf = true\n[remote \"origin\"]\n\turl = https://x/filter.git\n")]
    [InlineData("[core]\n\tfsmonitor =\n")]
    [InlineData("[user]\n\tname = [filter \"x\"]\n")]
    public async Task Inspect_CleanConfigStillReads(string config)
    {
        Assert.Null(await GitRepoSafety.InspectConfigAsync(config));
    }

    [Theory]
    [InlineData("[x][diff \"a b\"]\n\ttextconv = x\n", "diff")]
    [InlineData("[x][core]\n\talternateRefsCommand = x\n", "alternateRefsCommand")]
    [InlineData("[core]\n\tbare = false\n[x\n", "inválida")]
    public async Task InspectLog_UsesGitParser(string config, string expected)
    {
        var reason = await GitRepoSafety.InspectLogConfigAsync(config);

        Assert.NotNull(reason);
        Assert.Contains(expected, reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigReader_CachesByPathSizeAndMtime()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("config");
        File.WriteAllText(file, "[core]\n\tbare = false\n");
        var reader = new GitConfigReader(GitExecutable.Find());

        Assert.NotNull(await reader.ReadAsync(file));
        Assert.NotNull(await reader.ReadAsync(file));
        Assert.Equal(1, reader.ProcessCount);

        File.WriteAllText(file, "[core]\n\tbare = false\n\tautocrlf = true\n");
        var entries = await reader.ReadAsync(file);
        Assert.Equal(2, reader.ProcessCount);
        Assert.Contains(entries!, e => e.Key == "core.autocrlf" && e.Value == "true");
    }

    [Fact]
    public async Task GitDirJunction_IsNotFollowed()
    {
        using var repo = GitTestRepo.CreateWithCommit();
        var other = repo.Temp.Combine("outro");
        Directory.CreateDirectory(other);
        Junction(Path.Combine(other, ".git"), Path.Combine(repo.Path, ".git"));

        Assert.Null(GitRepositoryLocator.Locate(other));
        Assert.DoesNotContain(GitRepositoryLocator.FindChildRepos(repo.Temp.Path), l => l.RepoRoot.EndsWith("outro", StringComparison.OrdinalIgnoreCase));
        await Task.CompletedTask;
    }

    [Fact]
    public void StatusStartInfo_DropsInheritedGitVariables()
    {
        var previous = Environment.GetEnvironmentVariable("GIT_PAGER");
        Environment.SetEnvironmentVariable("GIT_PAGER", "evil"); // só usado com terminal: não afeta os outros testes
        try
        {
            var psi = GitStatusCache.BuildStartInfo(@"C:\git\git.exe", @"C:\repo");

            Assert.False(psi.Environment.ContainsKey("GIT_PAGER"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_PAGER", previous);
        }
    }

    internal static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
