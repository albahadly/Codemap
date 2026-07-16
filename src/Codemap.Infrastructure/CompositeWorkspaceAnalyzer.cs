using Codemap.Application.Abstractions;
using Codemap.Domain;
using Codemap.Infrastructure.JavaScript;
using Codemap.Infrastructure.Roslyn;

namespace Codemap.Infrastructure;

/// <summary>
/// The single IWorkspaceAnalyzer the application layer sees. Runs the Roslyn and JS/TS engines as the
/// language hint dictates (both when no hint is given), splits the progress range between them, and
/// merges their results. Cross-language Invokes edges are resolved afterwards by the command handler.
/// </summary>
public sealed class CompositeWorkspaceAnalyzer(
    CSharpWorkspaceAnalyzer csharpAnalyzer,
    JavaScriptWorkspaceAnalyzer javaScriptAnalyzer) : IWorkspaceAnalyzer
{
    public async Task<AnalysisResult> AnalyzeAsync(
        string path,
        Language? languageHint,
        Func<ScanProgress, Task>? onProgress,
        CancellationToken ct = default)
    {
        var runCSharp = languageHint is null or Language.CSharp;
        var runJs = languageHint is null or Language.JavaScript or Language.TypeScript;

        var csharpResult = AnalysisResult.Empty;
        var jsResult = AnalysisResult.Empty;

        // ScanProgressBehavior owns 0% and 100%; the analyzers report within (2..95).
        if (runCSharp && runJs)
        {
            csharpResult = await csharpAnalyzer.AnalyzeAsync(path, onProgress, (2, 60), ct).ConfigureAwait(false);
            jsResult = await javaScriptAnalyzer.AnalyzeAsync(path, onProgress, (60, 95), ct).ConfigureAwait(false);
        }
        else if (runCSharp)
        {
            csharpResult = await csharpAnalyzer.AnalyzeAsync(path, onProgress, (2, 95), ct).ConfigureAwait(false);
        }
        else if (runJs)
        {
            jsResult = await javaScriptAnalyzer.AnalyzeAsync(path, onProgress, (2, 95), ct).ConfigureAwait(false);
        }

        // Node id schemes never collide across languages (symbol names vs file paths), but dedupe defensively.
        var nodes = csharpResult.Nodes.Concat(jsResult.Nodes)
            .GroupBy(n => n.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        return new AnalysisResult(
            nodes,
            [.. csharpResult.Edges, .. jsResult.Edges],
            [.. csharpResult.HttpEndpoints, .. jsResult.HttpEndpoints],
            [.. csharpResult.HttpCallSites, .. jsResult.HttpCallSites],
            [.. csharpResult.Warnings, .. jsResult.Warnings]);
    }
}
