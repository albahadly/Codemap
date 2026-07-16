using Codemap.Domain;

namespace Codemap.Application.Abstractions;

/// <summary>Per-file progress emitted while a scan runs.</summary>
public sealed record ScanProgress(int PercentComplete, string CurrentFile);

/// <summary>An ASP.NET controller action exposed over HTTP, found on the C# side of the graph.</summary>
public sealed record HttpEndpoint(string NodeId, string HttpMethod, string RouteTemplate);

/// <summary>An outbound HTTP call site (fetch/axios/$http) found on the JS/TS side of the graph.</summary>
public sealed record HttpCallSite(string NodeId, string HttpMethod, string Url, string SourceFile, int LineNumber);

/// <summary>
/// The raw output of analyzing one repository path: graph parts plus the HTTP facts the
/// cross-language resolver correlates afterwards, and any non-fatal warnings.
/// </summary>
public sealed record AnalysisResult(
    IReadOnlyList<CodeNode> Nodes,
    IReadOnlyList<CodeEdge> Edges,
    IReadOnlyList<HttpEndpoint> HttpEndpoints,
    IReadOnlyList<HttpCallSite> HttpCallSites,
    IReadOnlyList<string> Warnings)
{
    public static readonly AnalysisResult Empty = new([], [], [], [], []);
}

public interface IWorkspaceAnalyzer
{
    /// <param name="onProgress">Awaited per progress step so updates reach the UI in order; may be null.</param>
    Task<AnalysisResult> AnalyzeAsync(
        string path,
        Language? languageHint,
        Func<ScanProgress, Task>? onProgress,
        CancellationToken ct = default);
}
