using Monitoramento.Core.Launcher;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Tests.Launcher;

public sealed class LaunchCommandBuilderTests
{
    private const string SystemDir = @"C:\Windows\System32";

    private static SavedProcess Saved(string mode) => new()
    {
        Id = "s1",
        Name = "Meu \"dev\"",
        Command = "npm run dev",
        WorkingDirectory = @"C:\proj",
        Env = new Dictionary<string, string> { ["PORT"] = "3000" },
        LaunchMode = mode,
    };

    [Fact]
    public void Build_HiddenRunsCmdSlashCWithoutWindow()
    {
        var spec = LaunchCommandBuilder.Build(Saved(LaunchModes.Hidden), SystemDir);

        Assert.Equal(@"C:\Windows\System32\cmd.exe", spec.ApplicationName);
        Assert.Equal("\"C:\\Windows\\System32\\cmd.exe\" /s /c \"npm run dev\"", spec.CommandLine);
        Assert.False(spec.NewConsole);
        Assert.Equal(@"C:\proj", spec.WorkingDirectory);
        Assert.Equal("3000", spec.Environment["PORT"]);
    }

    // S3: sem cmd externo nem "start": o pid raiz é o próprio cmd /k rastreado
    [Fact]
    public void Build_VisibleRunsCmdSlashKDirectlyInNewConsole()
    {
        var spec = LaunchCommandBuilder.Build(Saved(LaunchModes.Window), SystemDir);

        Assert.Equal(@"C:\Windows\System32\cmd.exe", spec.ApplicationName);
        Assert.Equal("\"C:\\Windows\\System32\\cmd.exe\" /s /k \"npm run dev\"", spec.CommandLine);
        Assert.DoesNotContain("start", spec.CommandLine, StringComparison.OrdinalIgnoreCase);
        Assert.True(spec.NewConsole);
        Assert.Equal("Meu dev", spec.Title);
        Assert.Equal("3000", spec.Environment["PORT"]);
    }
}
