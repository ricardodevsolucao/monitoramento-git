using Monitoramento.Core.Collection;

namespace Monitoramento.Core.Tests.Collection;

/// <summary>Q4: processo recém-criado ainda sem ProcessParameters não fica "sem acesso" para sempre.</summary>
public sealed class DetailsRetryTests
{
    private static RawProcess Raw(int pid) => new(pid, 1, "x.exe", 0, 0, 1);

    [Fact]
    public void Get_RetriesIncompleteReadsWithBackoff()
    {
        var reader = new ScriptedReader(incompleteReads: 2);
        var now = 0L;
        var provider = new CachedProcessDetailsProvider(reader, () => now);

        Assert.Null(provider.Get(Raw(7)).CommandLine);
        Assert.False(provider.Get(Raw(7)).AccessDenied); // abriu o processo: não é "sem acesso"
        Assert.Equal(1, reader.Reads); // sem nova tentativa antes do atraso

        now = 10_000;
        Assert.Null(provider.Get(Raw(7)).CommandLine);
        now = 20_000;
        var details = provider.Get(Raw(7));

        Assert.Equal("x.exe --a", details.CommandLine);
        Assert.Equal(@"C:\pasta", details.WorkingDirectory);
        Assert.Equal(3, reader.Reads);
    }

    [Fact]
    public void Get_GivesUpAfterFiveAttemptsAndMarksDenied()
    {
        var reader = new ScriptedReader(incompleteReads: int.MaxValue);
        var now = 0L;
        var provider = new CachedProcessDetailsProvider(reader, () => now);

        ProcessDetails details = provider.Get(Raw(7));
        for (var i = 0; i < 20; i++)
        {
            now += 60_000;
            details = provider.Get(Raw(7));
        }

        Assert.Equal(CachedProcessDetailsProvider.MaxReadAttempts, reader.Reads);
        Assert.True(details.AccessDenied);
    }

    [Fact]
    public void Get_DoesNotRetryWhenOpenProcessFailed()
    {
        var reader = new ScriptedReader(incompleteReads: 0) { Denied = true };
        var now = 0L;
        var provider = new CachedProcessDetailsProvider(reader, () => now);

        provider.Get(Raw(7));
        now = 60_000;
        provider.Get(Raw(7));

        Assert.Equal(1, reader.Reads);
    }

    private sealed class ScriptedReader(int incompleteReads) : IProcessReader
    {
        public int Reads { get; private set; }

        public bool Denied { get; init; }

        public ProcessDetails Read(int pid)
        {
            Reads++;
            if (Denied)
            {
                return new ProcessDetails(null, null, null, AccessDenied: true);
            }

            return Reads <= incompleteReads
                ? new ProcessDetails(@"C:\x.exe", null, null, AccessDenied: false, Incomplete: true)
                : new ProcessDetails(@"C:\x.exe", "x.exe --a", @"C:\pasta", AccessDenied: false);
        }

        public string? ReadWorkingDirectory(int pid) => @"C:\pasta";
    }
}
