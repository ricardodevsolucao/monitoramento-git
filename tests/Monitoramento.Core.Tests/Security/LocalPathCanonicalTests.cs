using System.Runtime.InteropServices;
using Monitoramento.Core.Git;
using Monitoramento.Core.Security;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Security;

/// <summary>
/// Forma canônica dos caminhos locais: letra de unidade maiúscula e nome longo no lugar do 8.3, para o mesmo repo não
/// aparecer duas vezes (c:\…\app e C:\…\app; RICARD~1.MAR e ricardo.martinez).
/// </summary>
public sealed class LocalPathCanonicalTests
{
    [Theory]
    [InlineData(@"c:\src\app", @"C:\src\app")]
    [InlineData(@"c:/src/app", @"C:\src\app")]
    [InlineData(@"C:\src\app", @"C:\src\app")]
    public void TryGetLocal_UppercasesDriveLetter(string input, string expected)
    {
        Assert.True(LocalPath.TryGetLocal(input, out var full));
        Assert.Equal(expected, full);
    }

    [Fact]
    public void TryGetLocal_ExpandsShortNameToLongName()
    {
        using var temp = new TempDirectory();
        var longDir = Directory.CreateDirectory(temp.Combine("nome comprido de pasta")).FullName;
        var shortDir = ShortPath(longDir);
        if (shortDir is null || string.Equals(shortDir, longDir, StringComparison.OrdinalIgnoreCase))
        {
            return; // volume sem nomes 8.3: nada a canonicalizar
        }

        Assert.True(LocalPath.TryGetLocal(shortDir, out var full));
        Assert.Equal(longDir, full);
        Assert.Equal(longDir, KnownGitRepos.Normalize(shortDir + "\\"));
    }

    [Fact]
    public void TryGetLocal_NonexistentShortLookingPath_IsKeptAsIs()
    {
        Assert.True(LocalPath.TryGetLocal(@"C:\NAOEXI~1\pasta", out var full));
        Assert.Equal(@"C:\NAOEXI~1\pasta", full);
    }

    private static string? ShortPath(string path)
    {
        var buffer = new char[1024];
        var length = GetShortPathName(path, buffer, (uint)buffer.Length);
        return length == 0 || length > buffer.Length ? null : new string(buffer, 0, (int)length);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, [Out] char[] shortPath, uint bufferLength);
}
