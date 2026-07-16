using Codemap.Application.Abstractions;
using Codemap.Domain;

namespace Codemap.Infrastructure.CrossLanguage;

/// <summary>
/// Correlates JS/TS HTTP call sites with ASP.NET controller routes by normalized route pattern:
/// case-insensitive, origin/query stripped, and every parameter segment ({id}, {id:int}, {expr},
/// :param) collapsed to "{}" so parameter names never matter. Matches emit Invokes edges with
/// Detail = "METHOD /route"; unmatched call sites surface as warnings.
/// </summary>
public sealed class CrossLanguageEdgeResolver : ICrossLanguageEdgeResolver
{
    public CrossLanguageResolution Resolve(IReadOnlyList<HttpEndpoint> endpoints, IReadOnlyList<HttpCallSite> callSites)
    {
        var edges = new List<CodeEdge>();
        var warnings = new List<string>();

        var endpointIndex = endpoints
            .GroupBy(e => (e.HttpMethod.ToUpperInvariant(), NormalizeRoute(e.RouteTemplate)))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var callSite in callSites)
        {
            var key = (callSite.HttpMethod.ToUpperInvariant(), NormalizeRoute(callSite.Url));
            if (endpointIndex.TryGetValue(key, out var matches))
            {
                foreach (var endpoint in matches)
                {
                    edges.Add(new CodeEdge(
                        callSite.NodeId,
                        endpoint.NodeId,
                        EdgeKind.Invokes,
                        $"{endpoint.HttpMethod} /{endpoint.RouteTemplate.TrimStart('/')}"));
                }
            }
            else
            {
                warnings.Add(
                    $"Unmatched HTTP call: {callSite.HttpMethod} {callSite.Url} " +
                    $"({callSite.SourceFile}:{callSite.LineNumber}) has no matching C# route.");
            }
        }

        return new CrossLanguageResolution(edges.Distinct().ToList(), warnings);
    }

    /// <summary>Normalizes a route/URL to a comparable pattern, e.g. "api/items/{}".</summary>
    public static string NormalizeRoute(string routeOrUrl)
    {
        var value = routeOrUrl.Trim();

        // Strip origin ("https://host:port") and query/fragment — we only compare paths.
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
            value = absolute.AbsolutePath;
        var cut = value.IndexOfAny(['?', '#']);
        if (cut >= 0) value = value[..cut];

        var segments = value.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var normalized = segments.Select(segment =>
            segment.StartsWith('{') || segment.StartsWith(':')
                ? "{}"
                : segment.ToLowerInvariant());
        return string.Join('/', normalized);
    }
}
