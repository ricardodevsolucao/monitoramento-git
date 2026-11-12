using System.Text.Json;
using Monitoramento.Core.Bridge;
using Monitoramento.Core.Interop;

namespace Monitoramento.Core.Git;

/// <summary>
/// Nomes dos scripts do <c>package.json</c> na raiz de um repositório (<c>projectScripts</c>). Só lê: nada é
/// executado. Não segue link/junção, ignora arquivo acima de 1 MB e JSON inválido vira lista vazia.
/// </summary>
internal static class PackageScripts
{
    public const int MaxScripts = 30;
    public const long MaxFileBytes = 1024 * 1024;

    private const int MaxScriptNameLength = 200;

    /// <summary>Esses vêm primeiro, nesta ordem; o resto em ordem alfabética.</summary>
    private static readonly string[] Preferred = ["dev", "start", "start:dev", "serve"];

    private static readonly string[] KnownManagers = ["npm", "pnpm", "yarn"];

    /// <summary>Lockfile → gerenciador, na ordem de preferência.</summary>
    private static readonly (string File, string Manager)[] Lockfiles =
    [
        ("pnpm-lock.yaml", "pnpm"),
        ("yarn.lock", "yarn"),
        ("package-lock.json", "npm"),
    ];

    /// <param name="repoRoot">Raiz já validada (local, repositório de verdade).</param>
    public static RepoScripts Read(string repoRoot)
    {
        if (GitRepositoryLocator.IsLink(repoRoot))
        {
            return new RepoScripts(repoRoot, null, []);
        }

        // abre sem seguir link e confere pelo próprio handle (sem janela entre checar e ler)
        if (FileSystemNative.OpenRegularFileNoFollow(Path.Combine(repoRoot, "package.json")) is not { } opened)
        {
            return new RepoScripts(repoRoot, null, []); // ausente, link/junção ou ilegível
        }

        (string? PackageManager, IReadOnlyList<string> Scripts) parsed;
        using (var stream = new FileStream(opened.Handle, FileAccess.Read))
        {
            parsed = opened.Size > MaxFileBytes ? (null, []) : Parse(stream, (int)opened.Size);
        }

        return new RepoScripts(repoRoot, parsed.PackageManager ?? FromLockfile(repoRoot), parsed.Scripts);
    }

    /// <param name="size">Tamanho lido do handle (até 1 MB); se o arquivo crescer depois, o excedente é ignorado.</param>
    private static (string? PackageManager, IReadOnlyList<string> Scripts) Parse(FileStream stream, int size)
    {
        try
        {
            var buffer = new byte[size];
            var read = stream.ReadAtLeast(buffer, size, throwOnEndOfStream: false);

            using var document = JsonDocument.Parse(buffer.AsMemory(0, read), new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, []);
            }

            return (ManagerFromField(root), ScriptNames(root));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, []);
        }
    }

    private static string? ManagerFromField(JsonElement root)
    {
        if (!root.TryGetProperty("packageManager", out var field) || field.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = field.GetString() ?? string.Empty;
        var at = value.IndexOf('@', StringComparison.Ordinal);
        var name = (at >= 0 ? value[..at] : value).Trim().ToLowerInvariant();
        return Array.IndexOf(KnownManagers, name) >= 0 ? name : null;
    }

    private static List<string> ScriptNames(JsonElement root)
    {
        if (!root.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return scripts.EnumerateObject()
            .Select(p => p.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n) && n.Length <= MaxScriptNameLength && !n.Any(char.IsControl))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(Rank)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n, StringComparer.Ordinal)
            .Take(MaxScripts)
            .ToList();
    }

    private static int Rank(string name)
    {
        var index = Array.IndexOf(Preferred, name);
        return index >= 0 ? index : Preferred.Length;
    }

    private static string? FromLockfile(string repoRoot)
    {
        foreach (var (file, manager) in Lockfiles)
        {
            if (File.Exists(Path.Combine(repoRoot, file)))
            {
                return manager;
            }
        }

        return null;
    }
}
