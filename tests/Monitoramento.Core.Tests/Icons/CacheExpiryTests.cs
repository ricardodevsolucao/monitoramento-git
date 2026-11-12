using Monitoramento.Core.Grouping;
using Monitoramento.Core.Icons;
using Monitoramento.Core.Tests.TestSupport;

namespace Monitoramento.Core.Tests.Icons;

/// <summary>Q11: caches de ícone e nome amigável não guardam "sem resultado" para sempre e podam executáveis sumidos.</summary>
public sealed class CacheExpiryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _time = new();

    public void Dispose() => _temp.Dispose();

    private string Exe(string name)
    {
        var path = _temp.Combine(name);
        File.WriteAllBytes(path, [0x4D, 0x5A]);
        return path;
    }

    [Fact]
    public void Icon_NegativeResultExpiresAfterOneMinute()
    {
        var exe = Exe("a.exe");
        var calls = 0;
        var icons = new IconProvider(_time, _ => ++calls == 1 ? null : [1, 2, 3]);

        Assert.Null(icons.GetPng(exe));
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(icons.GetPng(exe));
        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(new byte[] { 1, 2, 3 }, icons.GetPng(exe));
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.NotNull(icons.GetPng(exe));

        Assert.Equal(2, calls);
    }

    [Fact]
    public void Icon_PrunesExecutablesNotSeenForTenMinutes()
    {
        var a = Exe("a.exe");
        var b = Exe("b.exe");
        var icons = new IconProvider(_time, _ => [1]);

        icons.GetPng(a);
        icons.GetPng(b);
        _time.Advance(TimeSpan.FromMinutes(6));
        icons.GetPng(b);
        _time.Advance(TimeSpan.FromMinutes(5));
        icons.GetPng(b);

        Assert.Equal(1, icons.Count);
    }

    [Fact]
    public void FriendlyName_NegativeResultExpiresAfterOneMinute()
    {
        var exe = Exe("app.exe");
        var calls = 0;
        var names = new FileVersionNameProvider(_time, _ => ++calls == 1 ? null : "Aplicativo");

        Assert.Equal("app", names.Resolve("app.exe", exe));
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("app", names.Resolve("app.exe", exe));
        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("Aplicativo", names.Resolve("app.exe", exe));
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("Aplicativo", names.Resolve("app.exe", exe));

        Assert.Equal(2, calls);
    }

    [Fact]
    public void FriendlyName_PrunesExecutablesNotSeenForTenMinutes()
    {
        var names = new FileVersionNameProvider(_time, _ => "X");

        names.Resolve("a.exe", Exe("a.exe"));
        var b = Exe("b.exe");
        names.Resolve("b.exe", b);
        _time.Advance(TimeSpan.FromMinutes(6));
        names.Resolve("b.exe", b);
        _time.Advance(TimeSpan.FromMinutes(5));
        names.Resolve("b.exe", b);

        Assert.Equal(1, names.Count);
    }

    [Fact]
    public void FriendlyName_IgnoresNonLocalPath()
    {
        var calls = 0;
        var names = new FileVersionNameProvider(_time, _ => { calls++; return "X"; });

        Assert.Equal("app", names.Resolve("app.exe", @"\\host\share\app.exe"));
        Assert.Equal(0, calls);
    }
}
