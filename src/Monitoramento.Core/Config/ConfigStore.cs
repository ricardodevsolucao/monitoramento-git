using System.Text.Json;
using Monitoramento.Core.Bridge;
using Monitoramento.Core.Favorites;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Config;

/// <summary>
/// Lê e grava a configuração do usuário (padrão: %APPDATA%\Monitoramento\config.json).
/// Gravação atômica (arquivo temporário + replace). Arquivo corrompido vira ".bak" e a carga usa os padrões,
/// registrando o motivo em <see cref="LastLoadError"/>. Arquivo que existe mas não pôde ser lido (após novas
/// tentativas) marca <see cref="LastLoadFailed"/>.
/// </summary>
internal sealed class ConfigStore
{
    private readonly Lock _gate = new();

    public ConfigStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
    }

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Monitoramento", "config.json");

    public string Path { get; }

    public string? LastLoadError { get; private set; }

    /// <summary>
    /// A última carga não conseguiu ler um arquivo existente (travado, sem permissão). Os padrões devolvidos não
    /// representam o arquivo: quem usa não pode gravar por cima.
    /// </summary>
    public bool LastLoadFailed { get; private set; }

    /// <summary>Atrasos entre as novas tentativas de leitura (3 tentativas extras).</summary>
    internal TimeSpan[] RetryDelays { get; init; } = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)];

    public AppConfig Load()
    {
        lock (_gate)
        {
            LastLoadError = null;
            LastLoadFailed = false;
            if (!File.Exists(Path))
            {
                return new AppConfig();
            }

            if (!TryReadWithRetry(out var text))
            {
                LastLoadFailed = true;
                LastLoadError = "Não foi possível ler a configuração (o arquivo pode estar em uso por outro programa). "
                    + "As alterações ficam bloqueadas até a leitura funcionar, para não sobrescrever o arquivo.";
                return new AppConfig();
            }

            if (text is null)
            {
                return new AppConfig(); // sumiu entre o Exists e a leitura: equivale a "não existe"
            }

            try
            {
                var config = JsonSerializer.Deserialize(text, MonitorJsonContext.Default.AppConfig);
                var warnings = new List<string>();
                var sanitized = Sanitize(config, warnings);
                if (warnings.Count > 0)
                {
                    LastLoadError = "Configuração carregada com ajustes: " + string.Join(" ", warnings);
                }

                return sanitized;
            }
            catch (JsonException ex)
            {
                var where = ex.LineNumber is { } line ? $" na linha {line + 1}" : string.Empty;
                var backup = Path + ".bak";
                try
                {
                    File.Copy(Path, backup, overwrite: true);
                    LastLoadError = $"Configuração corrompida{where}. Cópia salva em \"{backup}\"; usando padrões.";
                }
                catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
                {
                    LastLoadError = $"Configuração corrompida{where} e não foi possível criar o backup; usando padrões.";
                }

                return new AppConfig();
            }
        }
    }

    /// <param name="text">Conteúdo, ou null se o arquivo deixou de existir.</param>
    /// <returns>false se o arquivo existe mas não pôde ser lido depois das novas tentativas.</returns>
    private bool TryReadWithRetry(out string? text)
    {
        text = null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                text = File.ReadAllText(Path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    return true;
                }

                if (attempt >= RetryDelays.Length)
                {
                    System.Diagnostics.Trace.TraceWarning($"Monitoramento: não foi possível ler \"{Path}\": {ex.GetType().Name}");
                    return false;
                }

                Thread.Sleep(RetryDelays[attempt]);
            }
        }
    }

    public void Save(AppConfig config)
    {
        lock (_gate)
        {
            var directory = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(directory);

            var temp = Path + ".tmp";
            var json = JsonSerializer.SerializeToUtf8Bytes(config, ConfigFileJsonContext.Default.AppConfig);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(Path))
            {
                try
                {
                    File.Replace(temp, Path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    return;
                }
                catch (IOException)
                {
                    // alguns sistemas de arquivos não suportam Replace: cai para Move com overwrite
                }
                catch (UnauthorizedAccessException)
                {
                    // idem
                }
            }

            File.Move(temp, Path, overwrite: true);
        }
    }

    /// <summary>Remove itens inválidos e corrige valores fora do esperado sem perder o resto.</summary>
    /// <param name="warnings">Avisos em português do que foi descartado por limite.</param>
    private static AppConfig Sanitize(AppConfig? config, ICollection<string> warnings)
    {
        if (config is null)
        {
            return new AppConfig();
        }

        var favorites = (config.Favorites ?? [])
            .Where(FavoriteKeys.IsValid)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var saved = (config.Saved ?? [])
            .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.Command))
            .Select(s => s with { Env = s.Env ?? new Dictionary<string, string>(), Name = s.Name ?? s.Command, LaunchMode = LaunchModes.Normalize(s.LaunchMode) })
            .DistinctBy(s => s.Id, StringComparer.Ordinal)
            .ToList();

        return new AppConfig
        {
            Favorites = favorites,
            Saved = saved,
            Settings = SettingsValidator.Sanitize(config.Settings),
            GitProjects = GitConfigSanitizer.Projects(config.GitProjects, warnings),
            GitWorkspace = GitConfigSanitizer.Workspace(config.GitWorkspace, warnings),
        };
    }
}
