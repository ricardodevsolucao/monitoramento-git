using Monitoramento.Core.Models;

namespace Monitoramento.Core.Config;

internal static class SettingsValidator
{
    public static readonly IReadOnlySet<string> Themes = new HashSet<string>(StringComparer.Ordinal) { "system", "light", "dark" };

    /// <returns>Mensagem de erro em português, ou null se válido.</returns>
    public static string? Validate(Settings? settings)
    {
        if (settings is null)
        {
            return "Configurações ausentes.";
        }

        if (settings.RefreshIntervalMs is < Settings.MinRefreshIntervalMs or > Settings.MaxRefreshIntervalMs)
        {
            return $"O intervalo de atualização deve ficar entre {Settings.MinRefreshIntervalMs} e {Settings.MaxRefreshIntervalMs} ms.";
        }

        if (settings.Theme is null || !Themes.Contains(settings.Theme))
        {
            return "Tema inválido. Use \"system\", \"light\" ou \"dark\".";
        }

        return null;
    }

    /// <summary>Corrige campo a campo o que estiver inválido, voltando ao padrão.</summary>
    public static Settings Sanitize(Settings? settings)
    {
        var defaults = new Settings();
        if (settings is null)
        {
            return defaults;
        }

        return settings with
        {
            RefreshIntervalMs = settings.RefreshIntervalMs is >= Settings.MinRefreshIntervalMs and <= Settings.MaxRefreshIntervalMs
                ? settings.RefreshIntervalMs
                : defaults.RefreshIntervalMs,
            Theme = settings.Theme is not null && Themes.Contains(settings.Theme) ? settings.Theme : defaults.Theme,
        };
    }
}
