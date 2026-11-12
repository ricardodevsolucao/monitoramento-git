using Microsoft.Web.WebView2.Core;
using Monitoramento.App.Hosting;
using Monitoramento.Core;

namespace Monitoramento.App;

internal static class Program
{
    internal const string WebViewDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    private static int _reportingError;

    [STAThread]
    private static void Main()
    {
        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportUnhandled("Exceção não tratada (UI)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("Exceção não tratada (domínio)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("Task com exceção não observada", e.Exception);
            e.SetObserved();
        };

        CrashLog.PruneOld();

        if (!IsWebViewRuntimeInstalled())
        {
            ShowMissingRuntime();
            return;
        }

        using var engine = new MonitorEngine(MonitorEngine.DefaultConfigPath);
        if (engine.ConfigLoadError is { } loadError)
        {
            // a UI mostra o aviso (ready.configLoadError); o log fica caso ela não carregue
            CrashLog.Write("Falha ao ler a configuração: " + loadError, null);
        }

        using var form = new MainForm(engine);
        instance.Listen(form.ShowFromTray);
        Application.Run(form);
    }

    private static bool IsWebViewRuntimeInstalled()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
    }

    internal static void ShowMissingRuntime()
    {
        var answer = MessageBox.Show(
            "O Monitoramento precisa do Microsoft Edge WebView2 Runtime, que não foi encontrado neste computador.\n\n" +
            "Baixe e instale em:\n" + WebViewDownloadUrl + "\n\nDeseja abrir a página de download agora?",
            "Monitoramento — componente ausente",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer == DialogResult.Yes)
        {
            MainForm.OpenDownloadPage();
        }
    }

    private static void ReportUnhandled(string context, Exception exception)
    {
        CrashLog.Write(context, exception);
        if (Interlocked.Exchange(ref _reportingError, 1) == 1)
        {
            return;
        }

        try
        {
            MessageBox.Show(
                "Ocorreu um erro inesperado, mas o aplicativo continua em execução.\nOs detalhes foram gravados em:\n" + AppPaths.Logs,
                "Monitoramento",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            Volatile.Write(ref _reportingError, 0);
        }
    }
}
