using System.Diagnostics;
using System.Text;
using Monitoramento.Core.Terminal;

namespace Monitoramento.Core.Tests.Terminal;

/// <summary>ConPTY de verdade (Windows 10 1809+): saída, entrada e encerramento da árvore.</summary>
public sealed class ConPtyIntegrationTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private sealed class Collector
    {
        private readonly StringBuilder _text = new();
        private readonly TerminalOutputBuffer _buffer = new();

        public ManualResetEventSlim Exited { get; } = new(false);

        public int ExitCode { get; private set; } = int.MinValue;

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public void Output(ReadOnlyMemory<byte> bytes)
        {
            lock (_text)
            {
                _text.Append(_buffer.Append(bytes.Span).Text);
            }
        }

        public void Exit(int code)
        {
            ExitCode = code;
            Exited.Set();
        }

        public bool WaitFor(string fragment)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < Timeout)
            {
                if (Text.Contains(fragment, StringComparison.Ordinal))
                {
                    return true;
                }

                Thread.Sleep(50);
            }

            return false;
        }
    }

    private static PtyStartInfo Info(string arguments, IReadOnlyDictionary<string, string>? env = null) =>
        new PtyStartInfo(Cmd, $"\"{Cmd}\" {arguments}", Environment.SystemDirectory, env ?? new Dictionary<string, string>(), 100, 30);

    [Fact]
    public void Factory_IsSupportedOnThisWindows()
    {
        Assert.True(new ConPtyFactory().IsSupported);
    }

    [Fact]
    public void Echo_OutputContainsText_AndExitCodeIsReported()
    {
        var collector = new Collector();
        using var session = new ConPtyFactory().Start(Info("/c echo oi-do-conpty& exit /b 7"), collector.Output, collector.Exit);

        Assert.True(session.WaitForExit(Timeout), "o cmd não terminou");
        Assert.Contains("oi-do-conpty", collector.Text, StringComparison.Ordinal);
        Assert.Equal(7, collector.ExitCode);
        Assert.True(session.Pid > 0);
        Assert.True(session.CreateTime > 0);
    }

    [Fact]
    public void Input_ExitEndsAnInteractiveCmd()
    {
        var collector = new Collector();
        using var session = new ConPtyFactory().Start(Info("/k echo pronto"), collector.Output, collector.Exit);
        Assert.True(collector.WaitFor("pronto"), collector.Text);

        session.Write("exit\r"u8);

        Assert.True(session.WaitForExit(Timeout), "exit não encerrou o cmd /k");
        Assert.Equal(0, collector.ExitCode);
    }

    [Fact]
    public void EnvironmentAndUtf8_ReachTheChild()
    {
        var collector = new Collector();
        var env = new Dictionary<string, string> { ["MONITORAMENTO_TESTE"] = "ação" };
        using var session = new ConPtyFactory().Start(Info("/c chcp 65001 >nul & echo [%MONITORAMENTO_TESTE%]", env), collector.Output, collector.Exit);

        Assert.True(session.WaitForExit(Timeout));
        Assert.Contains("[ação]", collector.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Kill_EndsTheWholeTree()
    {
        var collector = new Collector();
        using var session = new ConPtyFactory().Start(Info("/k ping -n 60 127.0.0.1"), collector.Output, collector.Exit);
        Assert.True(collector.WaitFor("127.0.0.1"), collector.Text);
        var pings = ChildPings(session.Pid);
        Assert.NotEmpty(pings);

        session.Kill();

        Assert.True(session.WaitForExit(Timeout));
        Assert.All(pings, pid => Assert.True(HasExited(pid), $"ping {pid} sobreviveu"));
    }

    [Fact]
    public void Resize_DoesNotThrowWhileRunning()
    {
        var collector = new Collector();
        using var session = new ConPtyFactory().Start(Info("/k echo pronto"), collector.Output, collector.Exit);
        Assert.True(collector.WaitFor("pronto"));

        session.Resize(140, 40);
        session.Kill();

        Assert.True(session.WaitForExit(Timeout));
    }

    private static List<int> ChildPings(int parentPid)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < Timeout)
        {
            var pings = (new Monitoramento.Core.Interop.NtProcessSource().ReadAll() ?? [])
                .Where(p => p.ParentPid == parentPid && string.Equals(p.Name, "PING.EXE", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Pid)
                .ToList();
            if (pings.Count > 0)
            {
                return pings;
            }

            Thread.Sleep(100);
        }

        return [];
    }

    private static bool HasExited(int pid)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < Timeout)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }
}
