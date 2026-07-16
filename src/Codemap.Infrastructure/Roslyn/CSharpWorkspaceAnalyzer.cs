using Codemap.Application.Abstractions;
using Codemap.Domain;
using Microsoft.CodeAnalysis;

namespace Codemap.Infrastructure.Roslyn;

/// <summary>
/// C#-side analysis: loads the workspace, walks symbols (nodes + HTTP endpoints), builds edges, then
/// consolidates against the node set so no dangling edges survive. Cross-project references inside one
/// solution resolve naturally because both sides format to the same symbol id.
/// </summary>
public sealed class CSharpWorkspaceAnalyzer(IWorkspaceLoader workspaceLoader)
{
    public async Task<AnalysisResult> AnalyzeAsync(
        string path,
        Func<ScanProgress, Task>? onProgress,
        (int From, int To) progressBand,
        CancellationToken ct = default)
    {
        using var loaded = await workspaceLoader.LoadAsync(path, ct).ConfigureAwait(false);
        var warnings = new List<string>(loaded.Warnings);
        if (loaded.Projects.Count == 0)
            return AnalysisResult.Empty with { Warnings = warnings };

        var rootPath = Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? path);
        var nodes = new Dictionary<string, CodeNode>(StringComparer.Ordinal);
        var endpoints = new List<HttpEndpoint>();
        var rawEdges = new List<CodeEdge>();

        var compilations = new List<Compilation>();
        foreach (var project in loaded.Projects)
        {
            ct.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null)
            {
                warnings.Add($"Project '{project.Name}' produced no compilation.");
                continue;
            }
            compilations.Add(compilation);
        }

        // Two passes per compilation (nodes, then edges) — progress is reported per file across both.
        var totalFiles = Math.Max(1, compilations.Sum(c => c.SyntaxTrees.Count()) * 2);
        var processed = 0;

        async Task ReportAsync(string file)
        {
            processed++;
            if (onProgress is null) return;
            var percent = progressBand.From + (int)((long)processed * (progressBand.To - progressBand.From) / totalFiles);
            await onProgress(new ScanProgress(Math.Min(percent, progressBand.To), Path.GetFileName(file))).ConfigureAwait(false);
        }

        foreach (var compilation in compilations)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                ct.ThrowIfCancellationRequested();
                await ReportAsync(tree.FilePath).ConfigureAwait(false);
                SymbolWalker.WalkTree(compilation, tree, nodes, endpoints, rootPath, ct);
            }
            foreach (var tree in compilation.SyntaxTrees)
            {
                ct.ThrowIfCancellationRequested();
                await ReportAsync(tree.FilePath).ConfigureAwait(false);
                CallGraphBuilder.BuildEdgesForTree(compilation, tree, rawEdges, ct);
            }
        }

        var edges = CallGraphBuilder.Consolidate(rawEdges, nodes.Keys.ToHashSet(StringComparer.Ordinal));
        return new AnalysisResult(nodes.Values.ToList(), edges, endpoints, [], warnings);
    }
}
