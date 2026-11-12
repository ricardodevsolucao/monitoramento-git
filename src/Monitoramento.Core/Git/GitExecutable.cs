namespace Monitoramento.Core.Git;

/// <summary>Localiza o git.exe no PATH e deriva a pasta de instalação do Git.</summary>
internal static class GitExecutable
{
    private static readonly Lazy<string?> Cached = new(Search);

    public static string? Find() => Cached.Value;

    /// <summary>"…\Git\cmd\git.exe", "…\Git\bin\git.exe" e "…\Git\mingw64\bin\git.exe" → "…\Git".</summary>
    public static string? InstallDirectoryOf(string? gitExe)
    {
        var dir = Path.GetDirectoryName(gitExe);
        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }

        var leaf = Path.GetFileName(dir);
        if (!leaf.Equals("cmd", StringComparison.OrdinalIgnoreCase) && !leaf.Equals("bin", StringComparison.OrdinalIgnoreCase))
        {
            return dir;
        }

        var parent = Path.GetDirectoryName(dir) ?? dir;
        var parentLeaf = Path.GetFileName(parent);
        var isNested = parentLeaf.Equals("mingw64", StringComparison.OrdinalIgnoreCase)
            || parentLeaf.Equals("mingw32", StringComparison.OrdinalIgnoreCase)
            || parentLeaf.Equals("usr", StringComparison.OrdinalIgnoreCase);
        return isNested ? Path.GetDirectoryName(parent) ?? parent : parent;
    }

    private static string? Search()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(entry.Trim('"'), "git.exe");
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (ArgumentException)
            {
                // entrada inválida no PATH: ignora
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var standard = Path.Combine(programFiles, "Git", "cmd", "git.exe");
        return File.Exists(standard) ? standard : null;
    }
}

/// <summary>Reconhece processos do git: git.exe, git-remote-*, git-credential-* e tudo sob a pasta de instalação do Git.</summary>
internal sealed class GitProcessClassifier(string? installDirectory)
{
    private readonly string? _installPrefix = string.IsNullOrEmpty(installDirectory)
        ? null
        : installDirectory.TrimEnd('\\', '/') + "\\";

    public static GitProcessClassifier CreateDefault() => new(GitExecutable.InstallDirectoryOf(GitExecutable.Find()));

    public bool IsGitProcess(string name, string? exePath)
    {
        if (name.Equals("git.exe", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("git-remote-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("git-credential-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _installPrefix is not null
            && exePath is not null
            && exePath.StartsWith(_installPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
