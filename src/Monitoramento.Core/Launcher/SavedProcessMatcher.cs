using Monitoramento.Core.Models;

namespace Monitoramento.Core.Launcher;

/// <summary>Processo raiz iniciado pelo app para um processo salvo.</summary>
internal sealed record LaunchRecord(int Pid, long CreateTime, DateTimeOffset StartedAt);

/// <summary>
/// Decide quais processos pertencem a um processo salvo: a árvore do pid raiz rastreado
/// e/ou processos com a mesma pasta de trabalho cuja linha de comando contém o comando salvo (normalizados,
/// em fronteira de token), sempre incluindo os descendentes.
/// </summary>
internal sealed class SavedProcessMatcher
{
    private readonly IReadOnlyList<ProcessInfo> _processes;
    private readonly ProcessTree _tree;
    private readonly string?[] _normalizedCommands;

    public SavedProcessMatcher(IReadOnlyList<ProcessInfo> processes)
    {
        _processes = processes;
        _tree = new ProcessTree(processes);
        _normalizedCommands = new string?[processes.Count];
    }

    /// <summary>
    /// Dono de cada processo quando mais de um salvo casa com ele: primeiro o que tem início registrado pelo app,
    /// depois o de comando mais longo, depois a ordem da lista.
    /// </summary>
    /// <returns>Para cada salvo (mesma ordem de <paramref name="savedList"/>), os processos que são dele.</returns>
    public IReadOnlyList<ProcessInfo>[] MatchAll(IReadOnlyList<SavedProcess> savedList, Func<string, LaunchRecord?> launchOf)
    {
        var candidates = new IReadOnlyList<ProcessInfo>[savedList.Count];
        var owner = new Dictionary<int, int>();
        for (var i = 0; i < savedList.Count; i++)
        {
            var launch = launchOf(savedList[i].Id);
            candidates[i] = Match(savedList[i], launch);
            foreach (var process in candidates[i])
            {
                if (!owner.TryGetValue(process.Pid, out var current) || Outranks(i, current))
                {
                    owner[process.Pid] = i;
                }
            }
        }

        var result = new IReadOnlyList<ProcessInfo>[savedList.Count];
        for (var i = 0; i < savedList.Count; i++)
        {
            var index = i;
            result[i] = candidates[i].Where(p => owner[p.Pid] == index).ToList();
        }

        return result;

        bool Outranks(int challenger, int current)
        {
            var challengerLaunched = launchOf(savedList[challenger].Id) is not null;
            var currentLaunched = launchOf(savedList[current].Id) is not null;
            if (challengerLaunched != currentLaunched)
            {
                return challengerLaunched;
            }

            var challengerLength = CommandText.Normalize(savedList[challenger].Command).Length;
            var currentLength = CommandText.Normalize(savedList[current].Command).Length;
            return challengerLength > currentLength;
        }
    }

    public IReadOnlyList<ProcessInfo> Match(SavedProcess saved, LaunchRecord? launch)
    {
        var matched = new Dictionary<int, ProcessInfo>();

        if (launch is not null)
        {
            var root = _processes.FirstOrDefault(p => p.Pid == launch.Pid && p.CreateTime == launch.CreateTime);
            if (root is not null)
            {
                matched[root.Pid] = root;
            }

            AddRange(matched, _tree.DescendantsOf(launch.Pid, launch.CreateTime));
        }

        var command = CommandText.Normalize(saved.Command);
        if (command.Length > 0 && !string.IsNullOrWhiteSpace(saved.WorkingDirectory))
        {
            for (var i = 0; i < _processes.Count; i++)
            {
                var process = _processes[i];
                if (matched.ContainsKey(process.Pid)
                    || process.CommandLine is null
                    || !PathText.AreEqual(process.WorkingDirectory, saved.WorkingDirectory))
                {
                    continue;
                }

                _normalizedCommands[i] ??= CommandText.Normalize(process.CommandLine);
                if (CommandText.ContainsToken(_normalizedCommands[i]!, command))
                {
                    matched[process.Pid] = process;
                    AddRange(matched, _tree.DescendantsOf(process.Pid, process.CreateTime));
                }
            }
        }

        return [.. matched.Values];
    }

    private static void AddRange(Dictionary<int, ProcessInfo> matched, List<ProcessInfo> processes)
    {
        foreach (var process in processes)
        {
            matched.TryAdd(process.Pid, process);
        }
    }
}
