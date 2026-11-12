using Monitoramento.Core.Models;

namespace Monitoramento.Core.Git;

/// <summary>Commit visto pelo layout: só o sha e os pais (primeiro pai = linha principal).</summary>
internal sealed record GraphNode(string Sha, IReadOnlyList<string> Parents);

/// <summary>Uma linha do gráfico: trilha do nó, linhas até a próxima linha e trilhas que só atravessam.</summary>
internal sealed record GraphRow(int Lane, IReadOnlyList<GitEdge> Edges, IReadOnlyList<int> PassThrough);

internal sealed record GraphLayout(IReadOnlyList<GraphRow> Rows, int Lanes);

/// <summary>
/// Layout clássico de trilhas (lanes), puro e determinístico. Cada trilha guarda o sha que ela "espera"; cada sha fica
/// reservado em no máximo uma trilha. O commit ocupa a trilha que o espera (ou a livre mais à esquerda, se for ponta);
/// o primeiro pai continua nessa trilha, e os demais (merge) vão para a trilha já reservada para eles ou para a livre
/// mais à esquerda. Pai já reservado em outra trilha → a linha converge para lá e a trilha atual fica livre.
/// Pais fora da janela continuam reservados até o fim (a linha segue descendo).
/// </summary>
internal static class GitGraphLayout
{
    public static GraphLayout Compute(IReadOnlyList<GraphNode> nodes)
    {
        var lanes = new List<string?>();
        var rows = new List<GraphRow>(nodes.Count);
        var maxLanes = 0;

        foreach (var node in nodes)
        {
            var lane = lanes.IndexOf(node.Sha);
            if (lane < 0)
            {
                lane = FreeLane(lanes);
            }

            var passThrough = new List<int>();
            for (var i = 0; i < lanes.Count; i++)
            {
                if (i != lane && lanes[i] is not null)
                {
                    passThrough.Add(i);
                }
            }

            lanes[lane] = null;
            var edges = new List<GitEdge>(node.Parents.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var p = 0; p < node.Parents.Count; p++)
            {
                var parent = node.Parents[p];
                if (!seen.Add(parent))
                {
                    continue;
                }

                var target = lanes.IndexOf(parent);
                if (target < 0)
                {
                    target = p == 0 ? lane : FreeLane(lanes);
                    lanes[target] = parent;
                }

                edges.Add(new GitEdge(lane, target, parent, p == 0 ? GitEdgeKind.Parent : GitEdgeKind.Merge));
            }

            var widest = Math.Max(lane, passThrough.Count > 0 ? passThrough[^1] : 0);
            foreach (var edge in edges)
            {
                widest = Math.Max(widest, edge.ToLane);
            }

            maxLanes = Math.Max(maxLanes, widest + 1);
            rows.Add(new GraphRow(lane, edges, passThrough));
        }

        return new GraphLayout(rows, maxLanes);
    }

    /// <summary>Trilha livre mais à esquerda (abre uma nova no fim se não houver).</summary>
    private static int FreeLane(List<string?> lanes)
    {
        var free = lanes.IndexOf(null);
        if (free >= 0)
        {
            return free;
        }

        lanes.Add(null);
        return lanes.Count - 1;
    }
}
