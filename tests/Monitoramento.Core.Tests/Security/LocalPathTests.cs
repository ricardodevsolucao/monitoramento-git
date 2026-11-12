using Monitoramento.Core.Security;

namespace Monitoramento.Core.Tests.Security;

/// <summary>S2: validador único de caminho local.</summary>
public sealed class LocalPathTests
{
    private static DriveType Fixed(string root) => DriveType.Fixed;

    [Theory]
    [InlineData(@"\\host\x")]
    [InlineData(@"\\host\share\pasta")]
    [InlineData("//host/x")]
    [InlineData(@"/\host\x")]
    [InlineData(@"\\?\C:\x")]
    [InlineData("//?/C:/x")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"\\.\C:\x")]
    [InlineData(@"\??\C:\x")]
    [InlineData(@"C:\x\GLOBALROOT\Device")]
    [InlineData(@"C:\pasta\CON")]
    [InlineData(@"C:\pasta\nul.txt")]
    [InlineData(@"C:\pasta\COM1\x")]
    [InlineData(@"C:\pasta\arquivo.txt:fluxo")]
    [InlineData("relativa")]
    [InlineData(@"\sem\unidade")]
    [InlineData("C:")]
    [InlineData("C:relativa")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("C:\\a\0b")]
    public void TryGetLocal_RefusesNonLocalPaths(string? path)
    {
        Assert.False(LocalPath.TryGetLocal(path, Fixed, out _));
    }

    [Theory]
    [InlineData(@"C:\Windows", @"C:\Windows")]
    [InlineData(@"C:\Windows\..\Users", @"C:\Users")]
    [InlineData("C:/proj/src", @"C:\proj\src")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\pasta\console.log", @"C:\pasta\console.log")]
    public void TryGetLocal_AcceptsAndNormalizesLocalPaths(string path, string expected)
    {
        Assert.True(LocalPath.TryGetLocal(path, Fixed, out var full));
        Assert.Equal(expected, full);
    }

    [Theory]
    [InlineData(DriveType.Fixed, true)]
    [InlineData(DriveType.Removable, true)]
    [InlineData(DriveType.Network, false)]
    [InlineData(DriveType.NoRootDirectory, false)]
    [InlineData(DriveType.CDRom, false)]
    [InlineData(DriveType.Ram, false)]
    [InlineData(DriveType.Unknown, false)]
    public void TryGetLocal_RequiresFixedOrRemovableDrive(DriveType type, bool expected)
    {
        Assert.Equal(expected, LocalPath.TryGetLocal(@"Z:\pasta", _ => type, out _));
    }

    [Fact]
    public void IsLocal_UsesRealDriveType()
    {
        Assert.True(LocalPath.IsLocal(Environment.SystemDirectory));
        Assert.False(LocalPath.IsLocal(@"\\host\share"));
    }
}
