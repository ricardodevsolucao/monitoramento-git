using System.Text.RegularExpressions;
using Monitoramento.Core.Favorites;

namespace Monitoramento.Core.Tests.Favorites;

public sealed class FavoriteKeysTests
{
    [Fact]
    public void ForApp_UsesLowercaseExeName()
    {
        Assert.Equal("app:code.exe", FavoriteKeys.ForApp("Code.EXE"));
    }

    [Fact]
    public void ForCommand_HasShortHexFormat()
    {
        var key = FavoriteKeys.ForCommand("node.exe", "node server.js");

        Assert.Matches(new Regex("^cmd:[0-9a-f]{12}$"), key);
    }

    [Fact]
    public void ForCommand_IsStableAcrossRuns()
    {
        // SHA-256("node.exe\nnode server.js") calculado fora do .NET (PowerShell), 12 primeiros hex.
        Assert.Equal("cmd:8096f678a352", FavoriteKeys.ForCommand("node.exe", "node server.js"));
    }

    [Fact]
    public void ForCommand_IgnoresCaseAndExtraWhitespace()
    {
        Assert.Equal(
            FavoriteKeys.ForCommand("node.exe", "node server.js"),
            FavoriteKeys.ForCommand("NODE.EXE", "  Node   server.js\t"));
    }

    [Fact]
    public void ForCommand_DiffersForDifferentCommands()
    {
        Assert.NotEqual(
            FavoriteKeys.ForCommand("node.exe", "node a.js"),
            FavoriteKeys.ForCommand("node.exe", "node b.js"));
    }

    [Fact]
    public void ForCommand_AcceptsMissingCommandLine()
    {
        Assert.StartsWith("cmd:", FavoriteKeys.ForCommand("x.exe", null));
    }

    [Theory]
    [InlineData("app:code.exe", true)]
    [InlineData("cmd:8096f678a352", true)]
    [InlineData("app:", false)]
    [InlineData("code.exe", false)]
    [InlineData("", false)]
    public void IsValid_AcceptsOnlyKnownPrefixes(string key, bool expected)
    {
        Assert.Equal(expected, FavoriteKeys.IsValid(key));
    }
}
