using Codemap.Domain;
using Codemap.Infrastructure.Roslyn;

namespace Codemap.Tests.Integration;

/// <summary>
/// End-to-end check of the C# engine against a real multi-project solution — Codemap itself
/// (spec §14: verify the analyzer on a real solution before trusting the UI). Loads the repository's
/// own .slnx through MSBuildWorkspace, so it exercises workspace loading, cross-project resolution,
/// SymbolWalker and CallGraphBuilder together.
/// </summary>
public class SelfScanIntegrationTests
{
    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Codemap.slnx")))
            dir = dir.Parent!;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    [Fact]
    public async Task Scanning_the_codemap_solution_produces_a_coherent_graph()
    {
        var root = FindRepositoryRoot();
        var analyzer = new CSharpWorkspaceAnalyzer(new MsBuildWorkspaceLoader());

        var result = await analyzer.AnalyzeAsync(root, onProgress: null, progressBand: (0, 100), CancellationToken.None);

        // Types from every layer of the solution must be present, proving cross-project loading.
        Assert.Contains(result.Nodes, n => n.Id == "Codemap.Domain.TopologyGraph");
        Assert.Contains(result.Nodes, n => n.Id == "Codemap.Application.Messaging.Dispatcher");
        Assert.Contains(result.Nodes, n => n.Id == "Codemap.Infrastructure.Roslyn.SymbolWalker");

        // Known relationships — the second and third cross project boundaries (Infrastructure→Application,
        // Application→Domain), proving symbols resolve across the solution.
        Assert.Contains(result.Edges, e =>
            e is { Kind: EdgeKind.Implements, FromId: "Codemap.Application.Messaging.Dispatcher" }
            && e.ToId == "Codemap.Application.Messaging.IDispatcher");
        Assert.Contains(result.Edges, e =>
            e is { Kind: EdgeKind.Implements, FromId: "Codemap.Infrastructure.CompositeWorkspaceAnalyzer" }
            && e.ToId == "Codemap.Application.Abstractions.IWorkspaceAnalyzer");
        Assert.Contains(result.Edges, e =>
            e is { Kind: EdgeKind.Calls, FromId: "Codemap.Application.Topology.GetTopologyDiffQueryHandler" }
            && e.ToId == "Codemap.Domain.Graph.TopologyDiffer");

        // No dangling edges: every endpoint resolves to a node.
        var ids = result.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(result.Edges, e =>
        {
            Assert.Contains(e.FromId, ids);
            Assert.Contains(e.ToId, ids);
        });

        // Partial/duplicate protection: node ids are unique.
        Assert.Equal(result.Nodes.Count, ids.Count);
    }
}
