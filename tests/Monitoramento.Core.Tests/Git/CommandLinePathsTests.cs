using Monitoramento.Core.Git;
using Monitoramento.Core.Security;

namespace Monitoramento.Core.Tests.Git;

public sealed class CommandLinePathsTests
{
    private static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\repo\app.js",
        @"C:\Program Files\nodejs\node.exe",
    };

    private static readonly HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\repo",
        @"C:\Program Files\nodejs",
        @"D:\work",
    };

    private static string? Find(string commandLine) =>
        CommandLinePaths.FindDirectory(commandLine, Dirs.Contains, Files.Contains, p => LocalPath.TryGetLocal(p, _ => DriveType.Fixed, out _));

    [Fact]
    public void FindDirectory_PrefersArgumentsOverExecutable()
    {
        Assert.Equal(@"C:\repo", Find("\"C:\\Program Files\\nodejs\\node.exe\" C:\\repo\\app.js --port 3000"));
    }

    [Fact]
    public void FindDirectory_AcceptsDirectoryArgument()
    {
        Assert.Equal(@"D:\work", Find("code.exe D:\\work"));
    }

    [Fact]
    public void FindDirectory_FallsBackToExecutableFolder()
    {
        Assert.Equal(@"C:\Program Files\nodejs", Find("\"C:\\Program Files\\nodejs\\node.exe\" --version"));
    }

    [Fact]
    public void FindDirectory_IgnoresRelativeAndMissingPaths()
    {
        Assert.Null(Find("node app.js E:\\nada\\x.js"));
    }
}
