using System.Globalization;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Git;

/// <summary>Resumo de <c>git status --porcelain=v2 --branch</c>. Ahead/behind nulos quando não há upstream.</summary>
internal sealed record GitStatusSummary(int DirtyCount, int? Ahead, int? Behind);

/// <summary>
/// <c>git status --porcelain=v2 --branch -z</c> completo: cabeçalhos da branch e cada mudança.
/// <paramref name="Oid"/> null em repositório sem commits; <paramref name="Head"/> "(detached)" em HEAD destacado.
/// </summary>
internal sealed record GitStatusDetail(
    string? Oid,
    string? Head,
    string? Upstream,
    int? Ahead,
    int? Behind,
    IReadOnlyList<GitChange> Changes);

internal static class PorcelainV2Parser
{
    private const string AheadBehindHeader = "# branch.ab ";

    public static GitStatusSummary Parse(string output)
    {
        var dirty = 0;
        int? ahead = null;
        int? behind = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 2)
            {
                continue;
            }

            if (line.StartsWith(AheadBehindHeader, StringComparison.Ordinal))
            {
                (ahead, behind) = ParseAheadBehind(line[AheadBehindHeader.Length..]);
                continue;
            }

            // 1 = alterado, 2 = renomeado/copiado, u = não mesclado, ? = não rastreado ("!" ignorado não conta)
            if (line[1] == ' ' && line[0] is ('1' or '2' or 'u' or '?'))
            {
                dirty++;
            }
        }

        return new GitStatusSummary(dirty, ahead, behind);
    }

    /// <summary>
    /// Saída com <c>-z</c>: registros separados por NUL; na renomeação/cópia ("2 …") o caminho de origem vem no
    /// registro seguinte. Caminhos chegam crus (sem aspas/escapes), por isso o <c>-z</c>.
    /// </summary>
    public static GitStatusDetail ParseDetailed(string output)
    {
        string? oid = null;
        string? head = null;
        string? upstream = null;
        int? ahead = null;
        int? behind = null;
        var changes = new List<GitChange>();

        var records = output.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.Length < 2 || record[1] != ' ')
            {
                continue;
            }

            switch (record[0])
            {
                case '#':
                    ParseHeader(record, ref oid, ref head, ref upstream, ref ahead, ref behind);
                    break;
                case '1' when Fields(record, 9) is { } f:
                    changes.Add(new GitChange(f[8], null, IndexState(f[1][0]), WorktreeState(f[1][1]), false));
                    break;
                case '2' when Fields(record, 10) is { } f:
                    var origin = i + 1 < records.Length ? records[++i] : null;
                    changes.Add(new GitChange(f[9], origin, IndexState(f[1][0]), WorktreeState(f[1][1]), false));
                    break;
                case 'u' when Fields(record, 11) is { } f:
                    changes.Add(new GitChange(f[10], null, GitIndexState.U, GitWorktreeState.U, true));
                    break;
                case '?':
                    changes.Add(new GitChange(record[2..], null, null, GitWorktreeState.Untracked, false));
                    break;
            }
        }

        return new GitStatusDetail(oid, head, upstream, ahead, behind, changes);
    }

    /// <summary>Divide em <paramref name="count"/> campos; o último (caminho) pode ter espaços. Null se faltar campo.</summary>
    private static string[]? Fields(string record, int count)
    {
        var fields = record.Split(' ', count);
        return fields.Length == count && fields[1].Length == 2 ? fields : null;
    }

    private static void ParseHeader(string record, ref string? oid, ref string? head, ref string? upstream, ref int? ahead, ref int? behind)
    {
        var parts = record.Split(' ', 3);
        if (parts.Length < 3)
        {
            return;
        }

        switch (parts[1])
        {
            case "branch.oid":
                oid = parts[2] == "(initial)" ? null : parts[2];
                break;
            case "branch.head":
                head = parts[2];
                break;
            case "branch.upstream":
                upstream = parts[2];
                break;
            case "branch.ab":
                (ahead, behind) = ParseAheadBehind(parts[2]);
                break;
        }
    }

    private static GitIndexState? IndexState(char c) => c switch
    {
        'M' => GitIndexState.M,
        'A' => GitIndexState.A,
        'D' => GitIndexState.D,
        'R' => GitIndexState.R,
        'C' => GitIndexState.C,
        'U' => GitIndexState.U,
        'T' => GitIndexState.T,
        _ => null,
    };

    private static GitWorktreeState? WorktreeState(char c) => c switch
    {
        'M' => GitWorktreeState.M,
        'D' => GitWorktreeState.D,
        'U' => GitWorktreeState.U,
        'T' => GitWorktreeState.T,
        _ => null,
    };

    private static (int? Ahead, int? Behind) ParseAheadBehind(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ahead)
            || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var behind))
        {
            return (null, null);
        }

        return (Math.Abs(ahead), Math.Abs(behind));
    }
}
