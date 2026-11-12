namespace Monitoramento.App.Hosting;

/// <summary>Pastas do app em %LOCALAPPDATA%\Monitoramento (estado local; a config do usuário fica em %APPDATA%).</summary>
internal static class AppPaths
{
    public static string LocalRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Monitoramento");

    public static string WebViewData => Path.Combine(LocalRoot, "WebView2");

    public static string Logs => Path.Combine(LocalRoot, "logs");

    public static string WindowFile => Path.Combine(LocalRoot, "window.json");

    public static string WwwRoot => Path.Combine(AppContext.BaseDirectory, "wwwroot");
}
