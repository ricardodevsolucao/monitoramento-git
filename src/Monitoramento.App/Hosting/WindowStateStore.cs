using System.Text.Json;

namespace Monitoramento.App.Hosting;

internal sealed record SavedWindow(int X, int Y, int Width, int Height, bool Maximized, bool TrayHintShown);

/// <summary>Lembra tamanho, posição e estado da janela em %LOCALAPPDATA%\Monitoramento\window.json.</summary>
internal static class WindowStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static SavedWindow? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.WindowFile))
            {
                return null;
            }

            return JsonSerializer.Deserialize<SavedWindow>(File.ReadAllText(AppPaths.WindowFile), Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            CrashLog.Write("window.json ilegível; usando o padrão", ex);
            return null;
        }
    }

    public static void Save(SavedWindow state)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LocalRoot);
            var temp = AppPaths.WindowFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
            File.Move(temp, AppPaths.WindowFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("Falha ao salvar window.json", ex);
        }
    }
}
