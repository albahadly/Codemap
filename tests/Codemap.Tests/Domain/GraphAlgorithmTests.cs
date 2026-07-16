using Codemap.Domain;
using Codemap.Domain.Graph;

namespace Codemap.Tests.Domain;

public class GraphAlgorithmTests
{
    private static CodeNode Node(string id, params string[] members) => new(
        id, id.Split('.')[^1], TypeKind.Class, Language.CSharp, "Fixture",
        members.Select(m => new MemberSignature(m, "void", false)).ToList(), "Fixture.cs", 1);

    private static TopologyGraph Graph(IEnumerable<CodeNode> nodes, IEnumerable<CodeEdge>? edges = null) =>
        new(Guid.NewGuid(), "fixture", DateTimeOffset.UtcNow, nodes.ToList(), (edges ?? []).ToList());

    // ── CycleDetector ────────────────────────────────────────────────────────────

    [Fact]
    public void Acyclic_graph_has_zero_cycles() =>
        Assert.Equal(0, CycleDetector.CountCycles(
        [
            new CodeEdge("A", "B", EdgeKind.Calls, null),
            new CodeEdge("B", "C", EdgeKind.Calls, null),
            new CodeEdge("A", "C", EdgeKind.References, null),
        ]));

    [Fact]
    public void Two_node_cycle_is_counted_once() =>
        Assert.Equal(1, CycleDetector.CountCycles(
        [
            new CodeEdge("A", "B", EdgeKind.Calls, null),
            new CodeEdge("B", "A", EdgeKind.Calls, null),
        ]));

    [Fact]
    public void Disjoint_cycles_are_counted_separately() =>
        Assert.Equal(2, CycleDetector.CountCycles(
        [
            new CodeEdge("A", "B", EdgeKind.Calls, null),
            new CodeEdge("B", "A", EdgeKind.Calls, null),
            new CodeEdge("X", "Y", EdgeKind.References, null),
            new CodeEdge("Y", "Z", EdgeKind.References, null),
            new CodeEdge("Z", "X", EdgeKind.References, null),
        ]));

    [Fact]
    public void Self_loop_counts_as_a_cycle() =>
        Assert.Equal(1, CycleDetector.CountCycles([new CodeEdge("A", "A", EdgeKind.Calls, null)]));

    // ── TopologyDiffer ───────────────────────────────────────────────────────────

    [Fact]
    public void Detects_added_and_removed_nodes()
    {
        var before = Graph([Node("F.Old"), Node("F.Kept")]);
        var after = Graph([Node("F.Kept"), Node("F.New")]);

        var diff = TopologyDiffer.Diff(before, after);

        Assert.Equal("F.New", Assert.Single(diff.Added).Id);
        Assert.Equal("F.Old", Assert.Single(diff.Removed).Id);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void Detects_member_changes_as_changed_nodes()
    {
        var before = Graph([Node("F.Svc", "Run()")]);
        var after = Graph([Node("F.Svc", "Run()", "Stop()")]);

        var diff = TopologyDiffer.Diff(before, after);

        var (b, a) = Assert.Single(diff.Changed);
        Assert.Single(b.Members);
        Assert.Equal(2, a.Members.Count);
    }

    [Fact]
    public void Line_number_only_changes_are_not_reported()
    {
        var node = Node("F.Svc", "Run()");
        var before = Graph([node]);
        var after = Graph([node with { LineNumber = 99, SourceFile = "Moved.cs" }]);

        var diff = TopologyDiffer.Diff(before, after);

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void Detects_edge_additions_and_removals()
    {
        var nodes = new[] { Node("F.A"), Node("F.B") };
        var before = Graph(nodes, [new CodeEdge("F.A", "F.B", EdgeKind.Calls, null)]);
        var after = Graph(nodes, [new CodeEdge("F.A", "F.B", EdgeKind.References, null)]);

        var diff = TopologyDiffer.Diff(before, after);

        Assert.Equal(EdgeKind.References, Assert.Single(diff.EdgesAdded).Kind);
        Assert.Equal(EdgeKind.Calls, Assert.Single(diff.EdgesRemoved).Kind);
    }
}
