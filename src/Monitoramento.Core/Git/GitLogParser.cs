using Monitoramento.Core.Models;

namespace Monitoramento.Core.Git;

/// <summary>Commit lido do <c>git log</c>, antes do layout das trilhas.</summary>
internal sealed record GitLogEntry(
    string Sha,
    IReadOnlyList<string> Parents,
    string Author,
    string Date,
    IReadOnlyList<GitRef> Refs,
    string Subject);

/// <summary>
/// Lê a saída de <c>git log -z --decorate=full --format=%H%x00%P%x00%an%x00%aI%x00%D%x00%s</c>: seis campos por
/// commit, todos separados por NUL (um commit não pode conter NUL, então qualquer texto no assunto é seguro).
/// </summary>
internal static class GitLogParser
{
    /// <summary>Formato correspondente ao parser (passar em <c>--format=</c> junto com <c>-z</c>).</summary>
    public const string Format = "%H%x00%P%x00%an%x00%aI%x00%D%x00%s";

    private const int FieldsPerCommit = 6;
    private const string HeadArrow = "HEAD -> ";
    private const string TagPrefix = "tag: ";
    private const string HeadsPrefix = "refs/heads/";
    private const string RemotesPrefix = "refs/remotes/";
    private const string TagsPrefix = "refs/tags/";

    public static IReadOnlyList<GitLogEntry> Parse(string output)
    {
        var fields = output.Split('\0');
        var entries = new List<GitLogEntry>(fields.Length / FieldsPerCommit);
        for (var i = 0; i + FieldsPerCommit <= fields.Length; i += FieldsPerCommit)
        {
            var sha = fields[i].Trim('\n', '\r');
            if (sha.Length == 0)
            {
                break;
            }

            entries.Add(new GitLogEntry(
                sha,
                fields[i + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                fields[i + 2],
                fields[i + 3],
                ParseRefs(fields[i + 4]),
                fields[i + 5]));
        }

        return entries;
    }

    /// <summary>
    /// "HEAD -> refs/heads/main, refs/remotes/origin/main, tag: refs/tags/v1": nomes de ref não têm espaço, então
    /// ", " separa com segurança. Branch atual vira "head"; HEAD destacado vira "HEAD"; remoto/HEAD e outras refs saem.
    /// </summary>
    public static IReadOnlyList<GitRef> ParseRefs(string decorations)
    {
        if (string.IsNullOrWhiteSpace(decorations))
        {
            return [];
        }

        var refs = new List<GitRef>();
        foreach (var raw in decorations.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "HEAD")
            {
                refs.Add(new GitRef("HEAD", GitRefKind.Head));
            }
            else if (raw.StartsWith(HeadArrow, StringComparison.Ordinal))
            {
                var target = raw[HeadArrow.Length..];
                refs.Add(new GitRef(target.StartsWith(HeadsPrefix, StringComparison.Ordinal) ? target[HeadsPrefix.Length..] : target, GitRefKind.Head));
            }
            else if (raw.StartsWith(TagPrefix, StringComparison.Ordinal))
            {
                var tag = raw[TagPrefix.Length..];
                refs.Add(new GitRef(tag.StartsWith(TagsPrefix, StringComparison.Ordinal) ? tag[TagsPrefix.Length..] : tag, GitRefKind.Tag));
            }
            else if (raw.StartsWith(HeadsPrefix, StringComparison.Ordinal))
            {
                refs.Add(new GitRef(raw[HeadsPrefix.Length..], GitRefKind.Branch));
            }
            else if (raw.StartsWith(RemotesPrefix, StringComparison.Ordinal) && !raw.EndsWith("/HEAD", StringComparison.Ordinal))
            {
                refs.Add(new GitRef(raw[RemotesPrefix.Length..], GitRefKind.Remote));
            }
        }

        return refs;
    }
}
