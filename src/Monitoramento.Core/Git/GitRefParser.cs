using System.Globalization;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Git;

/// <summary>Branches locais e remotas a partir de <c>git for-each-ref --format=<see cref="Format"/> refs/heads refs/remotes</c>.</summary>
internal static class GitRefParser
{
    /// <summary>Campos separados por NUL (<c>%00</c>), uma ref por linha (nomes de ref não têm NUL nem quebra de linha).</summary>
    public const string Format =
        "%(refname)%00%(refname:short)%00%(objectname)%00%(upstream:short)%00%(upstream:track,nobracket)%00"
        + "%(committerdate:iso-strict)%00%(HEAD)%00%(symref)";

    private const int FieldCount = 8;
    private const string RemotesPrefix = "refs/remotes/";

    public static IReadOnlyList<GitBranch> Parse(string output)
    {
        var branches = new List<GitBranch>();
        foreach (var line in output.Split('\n'))
        {
            var fields = line.TrimEnd('\r').Split('\0');
            if (fields.Length < FieldCount || fields[0].Length == 0 || fields[7].Length > 0)
            {
                continue; // linha vazia/incompleta ou ref simbólica (origin/HEAD)
            }

            var remote = fields[0].StartsWith(RemotesPrefix, StringComparison.Ordinal);
            var upstream = fields[3].Length > 0 ? fields[3] : null;
            var (ahead, behind) = upstream is null ? (null, null) : ParseTrack(fields[4]);
            branches.Add(new GitBranch(fields[1], fields[6] == "*", remote, upstream, ahead, behind, fields[2], fields[5]));
        }

        return branches;
    }

    /// <summary>"ahead 2, behind 1" / "ahead 2" / "behind 1" / "" (em dia) / "gone" (upstream apagado → nulos).</summary>
    private static (int? Ahead, int? Behind) ParseTrack(string track)
    {
        if (track.Length == 0)
        {
            return (0, 0);
        }

        int? ahead = 0;
        int? behind = 0;
        foreach (var part in track.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length != 2 || !int.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                return (null, null);
            }

            switch (pieces[0])
            {
                case "ahead":
                    ahead = count;
                    break;
                case "behind":
                    behind = count;
                    break;
                default:
                    return (null, null);
            }
        }

        return (ahead, behind);
    }
}
