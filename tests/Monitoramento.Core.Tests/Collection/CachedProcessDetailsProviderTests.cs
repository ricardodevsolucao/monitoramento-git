using Monitoramento.Core.Collection;

namespace Monitoramento.Core.Tests.Collection;

public sealed class CachedProcessDetailsProviderTests
{
    private static RawProcess Raw(int pid, long create) => new(pid, 1, "x.exe", 0, 0, create);

    [Fact]
    public void Get_CachesByPidAndCreateTime()
    {
        var reader = new CountingReader();
        var provider = new CachedProcessDetailsProvider(reader, () => 0);

        provider.Get(Raw(7, 1));
        provider.Get(Raw(7, 1));
        provider.Get(Raw(7, 2));

        Assert.Equal(2, reader.FullReads);
    }

    [Fact]
    public void Get_RereadsWorkingDirectoryAtMostEveryTenSeconds()
    {
        var reader = new CountingReader();
        var now = 0L;
        var provider = new CachedProcessDetailsProvider(reader, () => now);

        Assert.Equal(@"C:\inicio", provider.Get(Raw(7, 1)).WorkingDirectory);
        now = 9_000;
        Assert.Equal(@"C:\inicio", provider.Get(Raw(7, 1)).WorkingDirectory);
        now = 10_500;
        Assert.Equal(@"C:\depois", provider.Get(Raw(7, 1)).WorkingDirectory);

        Assert.Equal(1, reader.CwdReads);
    }

    [Fact]
    public void Get_DoesNotRetryDeniedProcesses()
    {
        var reader = new CountingReader { Deny = true };
        var now = 0L;
        var provider = new CachedProcessDetailsProvider(reader, () => now);

        provider.Get(Raw(7, 1));
        now = 60_000;
        var details = provider.Get(Raw(7, 1));

        Assert.True(details.AccessDenied);
        Assert.Equal(1, reader.FullReads);
        Assert.Equal(0, reader.CwdReads);
    }

    [Fact]
    public void Prune_ForgetsProcessesThatExited()
    {
        var reader = new CountingReader();
        var provider = new CachedProcessDetailsProvider(reader, () => 0);
        provider.Get(Raw(7, 1));

        provider.Prune([Raw(8, 1)]);
        provider.Get(Raw(7, 1));

        Assert.Equal(2, reader.FullReads);
    }

    private sealed class CountingReader : IProcessReader
    {
        public int FullReads { get; private set; }

        public int CwdReads { get; private set; }

        public bool Deny { get; init; }

        public ProcessDetails Read(int pid)
        {
            FullReads++;
            return Deny
                ? new ProcessDetails(null, null, null, AccessDenied: true)
                : new ProcessDetails(@"C:\x.exe", "x.exe --a", @"C:\inicio", AccessDenied: false);
        }

        public string? ReadWorkingDirectory(int pid)
        {
            CwdReads++;
            return @"C:\depois";
        }
    }
}
