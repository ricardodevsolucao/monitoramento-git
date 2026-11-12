using System.Diagnostics;

namespace Monitoramento.Core.Tests.TestSupport;

/// <summary>Repositório git real numa pasta temporária, criado com o git instalado.</summary>
internal sealed class GitTestRepo : IDisposable
{
    private readonly TempDirectory _temp = new();

    private GitTestRepo()
    {
        Path = _temp.Combine("repo");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public TempDirectory Temp => _temp;

    public static GitTestRepo CreateWithCommit()
    {
        var repo = new GitTestRepo();
        repo.Git("init", "-b", "main");
        File.WriteAllText(System.IO.Path.Combine(repo.Path, "readme.txt"), "ola");
        repo.Git("add", ".");
        repo.Git("-c", "user.name=Teste", "-c", "user.email=teste@exemplo.com", "commit", "-m", "inicial");
        return repo;
    }

    /// <summary>Repositório sem commits, com identidade local e sem conversão de fim de linha.</summary>
    public static GitTestRepo CreateEmpty()
    {
        var repo = new GitTestRepo();
        repo.Git("init", "-q", "-b", "main");
        repo.Git("config", "user.name", "Teste");
        repo.Git("config", "user.email", "teste@exemplo.com");
        repo.Git("config", "core.autocrlf", "false");
        return repo;
    }

    /// <summary>Bare numa pasta irmã (serve de "remoto" local para fetch/pull/push).</summary>
    public string CreateBareRemote(string name = "remoto.git")
    {
        var bare = _temp.Combine(name);
        RunGit(_temp.Path, "init", "-q", "--bare", "-b", "main", bare);
        return bare;
    }

    public string FullPath(string relative) => System.IO.Path.Combine(Path, relative);

    public void WriteFile(string relative, string content)
    {
        var full = FullPath(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Grava, prepara tudo e faz commit.</summary>
    public string CommitFile(string relative, string content, string message)
    {
        WriteFile(relative, content);
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
        return Git("rev-parse", "HEAD");
    }

    public string Git(params string[] args) => RunGit(Path, args);

    public static string RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git não iniciou");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} falhou: {error}");
        }

        return output.Trim();
    }

    public void Dispose() => _temp.Dispose();
}
