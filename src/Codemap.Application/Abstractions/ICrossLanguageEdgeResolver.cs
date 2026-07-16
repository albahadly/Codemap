using Codemap.Domain;

namespace Codemap.Application.Abstractions;

public sealed record CrossLanguageResolution(IReadOnlyList<CodeEdge> Edges, IReadOnlyList<string> Warnings);

public interface ICrossLanguageEdgeResolver
{
    /// <summary>
    /// Matches JS/TS HTTP call sites to C# controller routes and emits <see cref="EdgeKind.Invokes"/> edges.
    /// Unmatched call sites come back as warnings — they are never silently dropped.
    /// </summary>
    CrossLanguageResolution Resolve(IReadOnlyList<HttpEndpoint> endpoints, IReadOnlyList<HttpCallSite> callSites);
}
