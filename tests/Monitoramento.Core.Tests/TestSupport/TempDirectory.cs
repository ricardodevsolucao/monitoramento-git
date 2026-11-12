namespace Monitoramento.Core.Tests.TestSupport;

/// <summary>Pasta temporária exclusiva do teste, apagada no Dispose (inclusive arquivos somente leitura do git).</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MonitoramentoTests", Guid.NewGuid().ToString("N")[..12]);
        Path = System.IO.Path.GetFullPath(Directory.CreateDirectory(root).FullName);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(Path))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
