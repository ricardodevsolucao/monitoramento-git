using Monitoramento.Core.Models;

namespace Monitoramento.Core.Launcher;

/// <summary>
/// Navegação pai → filhos. Um filho só conta se foi criado depois do pai (CreateTime), para não seguir pids reaproveitados.
/// </summary>
internal sealed class ProcessTree
{
    private readonly Dictionary<int, List<ProcessInfo>> _children = [];

    public ProcessTree(IReadOnlyList<ProcessInfo> processes)
    {
        foreach (var process in processes)
        {
            if (process.ParentPid == process.Pid)
            {
                continue;
            }

            if (!_children.TryGetValue(process.ParentPid, out var list))
            {
                list = [];
                _children[process.ParentPid] = list;
            }

            list.Add(process);
        }
    }

    public static IReadOnlyList<ProcessInfo> Descendants(ProcessInfo root, IReadOnlyList<ProcessInfo> processes) =>
        new ProcessTree(processes).DescendantsOf(root.Pid, root.CreateTime);

    /// <summary>Todos os descendentes de (pid, createTime), mesmo que o próprio pai já tenha terminado.</summary>
    public List<ProcessInfo> DescendantsOf(int pid, long createTime)
    {
        var result = new List<ProcessInfo>();
        var visited = new HashSet<int> { pid };
        var pending = new Stack<(int Pid, long CreateTime)>();
        pending.Push((pid, createTime));

        while (pending.Count > 0)
        {
            var (parent, parentCreated) = pending.Pop();
            if (!_children.TryGetValue(parent, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (child.CreateTime < parentCreated || !visited.Add(child.Pid))
                {
                    continue;
                }

                result.Add(child);
                pending.Push((child.Pid, child.CreateTime));
            }
        }

        return result;
    }
}
