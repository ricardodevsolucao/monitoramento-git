using Monitoramento.Core.Git;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Git;

/// <summary>Leitura dos nomes de "scripts" do package.json (nada é executado).</summary>
public sealed class PackageScriptsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Repo(string name = "app")
    {
        var path = _temp.Combine(name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Scripts_AreNames_WithDevStartFirst_ThenAlphabetical()
    {
        var repo = Repo();
        File.WriteAllText(Path.Combine(repo, "package.json"), """
            { "name": "app", "scripts": { "lint": "eslint .", "build": "ng build", "serve": "x", "start:dev": "y",
              "start": "ng serve", "test": "vitest", "dev": "vite" } }
            """);

        var result = PackageScripts.Read(repo);

        Assert.Equal(repo, result.RepoRoot);
        Assert.Equal(["dev", "start", "start:dev", "serve", "build", "lint", "test"], result.Scripts);
        Assert.Null(result.PackageManager);
    }

    [Theory]
    [InlineData("pnpm@9.1.0", "pnpm")]
    [InlineData("yarn@4.0.0+sha256.abc", "yarn")]
    [InlineData("npm@10.0.0", "npm")]
    [InlineData("bun@1.0.0", null)]
    public void PackageManager_FromField(string field, string? expected)
    {
        var repo = Repo();
        File.WriteAllText(Path.Combine(repo, "package.json"), $$"""{ "packageManager": "{{field}}", "scripts": {} }""");

        Assert.Equal(expected, PackageScripts.Read(repo).PackageManager);
    }

    [Theory]
    [InlineData("pnpm-lock.yaml", "pnpm")]
    [InlineData("yarn.lock", "yarn")]
    [InlineData("package-lock.json", "npm")]
    public void PackageManager_FromLockfile(string lockfile, string expected)
    {
        var repo = Repo();
        File.WriteAllText(Path.Combine(repo, "package.json"), """{ "scripts": { "dev": "x" } }""");
        File.WriteAllText(Path.Combine(repo, lockfile), "");

        Assert.Equal(expected, PackageScripts.Read(repo).PackageManager);
    }

    [Fact]
    public void FieldWinsOverLockfile()
    {
        var repo = Repo();
        File.WriteAllText(Path.Combine(repo, "package.json"), """{ "packageManager": "yarn@1.22.0" }""");
        File.WriteAllText(Path.Combine(repo, "package-lock.json"), "{}");

        Assert.Equal("yarn", PackageScripts.Read(repo).PackageManager);
    }

    [Theory]
    [InlineData("{ isto não é json")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "scripts": [ "dev" ] }""")]
    [InlineData("""{ "scripts": "dev" }""")]
    public void InvalidJsonOrShape_GivesNoScripts(string content)
    {
        var repo = Repo();
        File.WriteAllText(Path.Combine(repo, "package.json"), content);

        Assert.Empty(PackageScripts.Read(repo).Scripts);
    }

    [Fact]
    public void MissingPackageJson_GivesNoScriptsAndNoManager()
    {
        var repo = Repo();

        var result = PackageScripts.Read(repo);

        Assert.Empty(result.Scripts);
        Assert.Null(result.PackageManager);
    }

    [Fact]
    public void FileOverOneMegabyte_IsNotRead()
    {
        var repo = Repo();
        var padding = new string(' ', (int)PackageScripts.MaxFileBytes);
        File.WriteAllText(Path.Combine(repo, "package.json"), """{ "scripts": { "dev": "x" } }""" + padding);

        Assert.Empty(PackageScripts.Read(repo).Scripts);
    }

    [Fact]
    public void AtMostThirtyScripts()
    {
        var repo = Repo();
        var entries = string.Join(", ", Enumerable.Range(0, 50).Select(i => $"\"s{i:00}\": \"x\""));
        File.WriteAllText(Path.Combine(repo, "package.json"), $"{{ \"scripts\": {{ {entries}, \"dev\": \"vite\" }} }}");

        var scripts = PackageScripts.Read(repo).Scripts;

        Assert.Equal(PackageScripts.MaxScripts, scripts.Count);
        Assert.Equal("dev", scripts[0]);
    }

    [Fact]
    public void PackageJsonJunctionedRepoFolder_IsNotFollowed()
    {
        var real = Repo("real");
        File.WriteAllText(Path.Combine(real, "package.json"), """{ "scripts": { "dev": "x" } }""");
        var link = _temp.Combine("link");
        GitConfigSafetyTests.Junction(link, real);

        Assert.Empty(PackageScripts.Read(link).Scripts);
    }

    [Fact]
    public void ScriptNamesWithControlCharacters_AreDropped()
    {
        var repo = Repo();
        File.WriteAllText(Path.Combine(repo, "package.json"), """{ "scripts": { "dev": "x", "ma\u001b[31mu": "y", "nova\nlinha": "z", "ok:ç": "w" } }""");

        Assert.Equal(["dev", "ok:ç"], PackageScripts.Read(repo).Scripts);
    }

    [Fact]
    public void PackageJsonFileSymlink_IsNotFollowed()
    {
        var real = Repo("real");
        var target = Path.Combine(real, "package.json");
        File.WriteAllText(target, """{ "scripts": { "dev": "x" } }""");
        var repo = Repo();
        try
        {
            File.CreateSymbolicLink(Path.Combine(repo, "package.json"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // sem privilégio para criar link simbólico (sem Modo de Desenvolvedor): nada a verificar
        }

        Assert.Empty(PackageScripts.Read(repo).Scripts);
    }

    [Fact]
    public void ReadsThroughOpenHandle_ExactContentRegardlessOfSize()
    {
        var repo = Repo();
        var entries = string.Join(", ", Enumerable.Range(0, 5).Select(i => $"\"s{i}\": \"{new string('x', 50_000)}\""));
        File.WriteAllText(Path.Combine(repo, "package.json"), $"{{ \"scripts\": {{ {entries} }} }}");

        Assert.Equal(["s0", "s1", "s2", "s3", "s4"], PackageScripts.Read(repo).Scripts);
    }
}
