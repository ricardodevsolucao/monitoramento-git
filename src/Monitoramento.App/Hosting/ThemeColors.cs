using System.Drawing;
using System.Security;
using Microsoft.Win32;

namespace Monitoramento.App.Hosting;

/// <summary>Fundo da UI (var(--bg) de app.css) por tema, para a janela nunca piscar branco.</summary>
internal static class ThemeColors
{
    private static readonly Color Light = Color.FromArgb(0xF1, 0xF4, 0xF7);
    private static readonly Color Dark = Color.FromArgb(0x0E, 0x14, 0x1A);

    public static bool IsDark(string theme) => theme switch
    {
        "dark" => true,
        "light" => false,
        _ => SystemUsesDarkApps(),
    };

    public static Color Background(bool dark) => dark ? Dark : Light;

    private static bool SystemUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
