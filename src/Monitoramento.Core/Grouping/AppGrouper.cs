using Monitoramento.Core.Favorites;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Grouping;

/// <summary>Agrupa processos por executável ("app:&lt;exe minúsculo&gt;") e ordena por memória.</summary>
internal static class AppGrouper
{
    public static IReadOnlyList<AppGroup> Build(
        IReadOnlyList<ProcessInfo> processes,
        long totalMemoryBytes,
        IReadOnlySet<string> favorites,
        IFriendlyNameProvider names,
        GroupMemoryHistory history)
    {
        var groups = new List<AppGroup>();

        foreach (var bucket in processes.GroupBy(p => FavoriteKeys.ForApp(p.Name), StringComparer.Ordinal))
        {
            var members = bucket
                .OrderByDescending(p => p.MemoryBytes)
                .ThenBy(p => p.Pid)
                .ToList();

            var withPath = members.FirstOrDefault(p => p.ExePath is not null);
            var exeName = (withPath ?? members[0]).Name;
            var exePath = withPath?.ExePath;
            var memory = members.Sum(p => p.MemoryBytes);

            groups.Add(new AppGroup
            {
                Key = bucket.Key,
                Name = names.Resolve(exeName, exePath),
                ExeName = exeName,
                ExePath = exePath,
                IconKey = exePath?.ToLowerInvariant(),
                MemoryBytes = memory,
                MemoryPercent = Percent(memory, totalMemoryBytes),
                CpuPercent = Math.Min(100, members.Sum(p => p.CpuPercent)),
                ProcessCount = members.Count,
                Favorite = favorites.Contains(bucket.Key),
                Repos = DistinctRepos(members),
                MemoryHistory = history.Push(bucket.Key, memory),
                Processes = members,
            });
        }

        groups.Sort(static (a, b) =>
        {
            var byMemory = b.MemoryBytes.CompareTo(a.MemoryBytes);
            return byMemory != 0 ? byMemory : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return groups;
    }

    public static double Percent(long part, long total) => total <= 0 ? 0 : part * 100.0 / total;

    private static List<GitInfo> DistinctRepos(List<ProcessInfo> members) =>
        members
            .SelectMany(p => p.Git is null ? p.SubRepos : [p.Git])
            .DistinctBy(g => g.RepoRoot, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
