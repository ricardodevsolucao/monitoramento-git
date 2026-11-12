using Monitoramento.Core.Git;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Tests.Git;

/// <summary>Layout das trilhas: entrada em ordem topológica (mais novo primeiro), uma linha por commit.</summary>
public sealed class GitGraphLayoutTests
{
    private static GraphNode N(string sha, params string[] parents) => new(sha, parents);

    private static GitEdge P(int from, int to, string sha) => new(from, to, sha, GitEdgeKind.Parent);

    private static GitEdge M(int from, int to, string sha) => new(from, to, sha, GitEdgeKind.Merge);

    private static void AssertRow(GraphRow row, int lane, GitEdge[] edges, int[] passThrough)
    {
        Assert.Equal(lane, row.Lane);
        Assert.Equal(edges, row.Edges);
        Assert.Equal(passThrough, row.PassThrough);
    }

    [Fact]
    public void Linear_StaysInLaneZero()
    {
        var layout = GitGraphLayout.Compute([N("a", "b"), N("b", "c"), N("c")]);

        Assert.Equal(1, layout.Lanes);
        AssertRow(layout.Rows[0], 0, [P(0, 0, "b")], []);
        AssertRow(layout.Rows[1], 0, [P(0, 0, "c")], []);
        AssertRow(layout.Rows[2], 0, [], []);
    }

    [Fact]
    public void BranchAndMerge_SecondParentOpensLaneThatJoinsBack()
    {
        var layout = GitGraphLayout.Compute([N("m", "a", "b"), N("a", "c"), N("b", "c"), N("c")]);

        Assert.Equal(2, layout.Lanes);
        AssertRow(layout.Rows[0], 0, [P(0, 0, "a"), M(0, 1, "b")], []);
        AssertRow(layout.Rows[1], 0, [P(0, 0, "c")], [1]);
        AssertRow(layout.Rows[2], 1, [P(1, 0, "c")], [0]);
        AssertRow(layout.Rows[3], 0, [], []);
    }

    [Fact]
    public void TwoMerges_UseThreeLanesAndConverge()
    {
        var layout = GitGraphLayout.Compute([N("m2", "m1", "d"), N("m1", "a", "b"), N("d", "a"), N("b", "a"), N("a")]);

        Assert.Equal(3, layout.Lanes);
        AssertRow(layout.Rows[0], 0, [P(0, 0, "m1"), M(0, 1, "d")], []);
        AssertRow(layout.Rows[1], 0, [P(0, 0, "a"), M(0, 2, "b")], [1]);
        AssertRow(layout.Rows[2], 1, [P(1, 0, "a")], [0, 2]);
        AssertRow(layout.Rows[3], 2, [P(2, 0, "a")], [0]);
        AssertRow(layout.Rows[4], 0, [], []);
    }

    [Fact]
    public void Octopus_ThreeParentsTakeThreeLanes()
    {
        var layout = GitGraphLayout.Compute([N("o", "a", "b", "c"), N("a"), N("b"), N("c")]);

        Assert.Equal(3, layout.Lanes);
        AssertRow(layout.Rows[0], 0, [P(0, 0, "a"), M(0, 1, "b"), M(0, 2, "c")], []);
        AssertRow(layout.Rows[1], 0, [], [1, 2]);
        AssertRow(layout.Rows[2], 1, [], [2]);
        AssertRow(layout.Rows[3], 2, [], []);
    }

    [Fact]
    public void FreedLane_IsReusedByNextTip()
    {
        var layout = GitGraphLayout.Compute([N("m", "a", "b"), N("b", "a"), N("a", "r"), N("t", "r"), N("r")]);

        Assert.Equal(2, layout.Lanes);
        AssertRow(layout.Rows[1], 1, [P(1, 0, "a")], [0]);
        AssertRow(layout.Rows[2], 0, [P(0, 0, "r")], []);
        AssertRow(layout.Rows[3], 1, [P(1, 0, "r")], [0]); // trilha 1 liberada por "b" e reaproveitada pela ponta "t"
        AssertRow(layout.Rows[4], 0, [], []);
    }

    [Fact]
    public void ParentOutsideWindow_KeepsEdgeGoingDown()
    {
        var layout = GitGraphLayout.Compute([N("a", "b"), N("b", "z"), N("t", "w")]);

        Assert.Equal(2, layout.Lanes);
        AssertRow(layout.Rows[1], 0, [P(0, 0, "z")], []);
        AssertRow(layout.Rows[2], 1, [P(1, 1, "w")], [0]); // "z" fora da janela: a trilha 0 continua descendo
    }

    /// <summary>
    /// Convenção da UI: (1) <c>toLane</c> é a trilha do pai na linha seguinte (o nó, se o pai for o próximo commit, ou
    /// uma trilha de passThrough); (2) passThrough da linha N segue reto para a N+1 (como nó ou passThrough);
    /// (3) nenhuma trilha nova surge na N+1 sem ter vindo de um passThrough ou de uma aresta da linha N.
    /// </summary>
    [Fact]
    public void Layout_FollowsUiConvention_OnComplexHistory()
    {
        GraphNode[] nodes =
        [
            N("t1", "m3"), N("t2", "c5"), N("m3", "c4", "x2", "y1"), N("x2", "x1"), N("c4", "m2"), N("y1", "c2"),
            N("c5", "m2"), N("m2", "c3", "x1"), N("x1", "c2"), N("c3", "c2"), N("c2", "c1"), N("t3", "fora"), N("c1"),
        ];

        var layout = GitGraphLayout.Compute(nodes);

        for (var n = 0; n < nodes.Length - 1; n++)
        {
            var row = layout.Rows[n];
            var next = layout.Rows[n + 1];
            foreach (var edge in row.Edges)
            {
                Assert.Equal(row.Lane, edge.FromLane);
                var isNextNode = nodes[n + 1].Sha == edge.ToSha && next.Lane == edge.ToLane;
                Assert.True(isNextNode || next.PassThrough.Contains(edge.ToLane), $"linha {n}: aresta para {edge.ToSha} na trilha {edge.ToLane}");
            }

            foreach (var lane in row.PassThrough)
            {
                Assert.True(next.Lane == lane || next.PassThrough.Contains(lane), $"linha {n}: passThrough {lane} não continua");
            }

            var arriving = row.PassThrough.Concat(row.Edges.Select(e => e.ToLane)).ToHashSet();
            Assert.True(arriving.Contains(next.Lane) || !nodes.Take(n + 1).Any(c => c.Parents.Contains(nodes[n + 1].Sha)), $"linha {n + 1}: nó sem linha chegando");
            Assert.All(next.PassThrough, lane => Assert.Contains(lane, arriving));
            Assert.DoesNotContain(row.Lane, row.PassThrough);
            Assert.All(row.PassThrough.Concat(row.Edges.Select(e => e.ToLane)).Append(row.Lane), lane => Assert.InRange(lane, 0, layout.Lanes - 1));
        }
    }

    [Fact]
    public void Empty_HasNoLanes()
    {
        var layout = GitGraphLayout.Compute([]);

        Assert.Empty(layout.Rows);
        Assert.Equal(0, layout.Lanes);
    }

    [Fact]
    public void DuplicateParent_DoesNotOpenSecondLane()
    {
        var layout = GitGraphLayout.Compute([N("m", "a", "a"), N("a")]);

        Assert.Equal(1, layout.Lanes);
        AssertRow(layout.Rows[0], 0, [P(0, 0, "a")], []);
    }
}
