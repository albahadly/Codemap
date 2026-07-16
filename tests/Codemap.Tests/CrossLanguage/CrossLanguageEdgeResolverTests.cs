using Codemap.Application.Abstractions;
using Codemap.Domain;
using Codemap.Infrastructure.CrossLanguage;

namespace Codemap.Tests.CrossLanguage;

public class CrossLanguageEdgeResolverTests
{
    private readonly CrossLanguageEdgeResolver _resolver = new();

    private static HttpEndpoint Endpoint(string method, string route, string nodeId = "Api.ScanController") =>
        new(nodeId, method, route);

    private static HttpCallSite CallSite(string method, string url, string nodeId = "src/api.ts#ApiClient") =>
        new(nodeId, method, url, "src/api.ts", 10);

    [Fact]
    public void Matching_method_and_route_emits_invokes_edge_with_detail()
    {
        var result = _resolver.Resolve(
            [Endpoint("POST", "api/scan")],
            [CallSite("POST", "/api/scan")]);

        var edge = Assert.Single(result.Edges);
        Assert.Equal(EdgeKind.Invokes, edge.Kind);
        Assert.Equal("src/api.ts#ApiClient", edge.FromId);
        Assert.Equal("Api.ScanController", edge.ToId);
        Assert.Equal("POST /api/scan", edge.Detail);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Route_matching_is_case_insensitive()
    {
        var result = _resolver.Resolve(
            [Endpoint("GET", "API/Items")],
            [CallSite("GET", "/api/items")]);

        Assert.Single(result.Edges);
    }

    [Fact]
    public void Route_parameter_names_are_ignored()
    {
        var result = _resolver.Resolve(
            [Endpoint("GET", "api/items/{id:int}")],
            [CallSite("GET", "api/items/{itemId}")]); // e.g. template literal `api/items/${itemId}`

        Assert.Single(result.Edges);
    }

    [Fact]
    public void Absolute_urls_and_query_strings_are_normalized_away()
    {
        var result = _resolver.Resolve(
            [Endpoint("GET", "api/items")],
            [CallSite("GET", "https://localhost:5001/api/items?page=2")]);

        Assert.Single(result.Edges);
    }

    [Fact]
    public void Method_mismatch_does_not_match_and_surfaces_a_warning()
    {
        var result = _resolver.Resolve(
            [Endpoint("POST", "api/scan")],
            [CallSite("GET", "/api/scan")]);

        Assert.Empty(result.Edges);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("GET /api/scan", warning);
        Assert.Contains("src/api.ts:10", warning);
    }

    [Fact]
    public void Unmatched_call_sites_are_reported_not_dropped()
    {
        var result = _resolver.Resolve(
            [],
            [CallSite("GET", "/api/unknown")]);

        Assert.Empty(result.Edges);
        Assert.Single(result.Warnings);
    }

    [Theory]
    [InlineData("api/items/{id}", "api/items/{}")]
    [InlineData("/API/Items/", "api/items")]
    [InlineData("https://host:1234/api/x?y=1#z", "api/x")]
    [InlineData("api/items/:id", "api/items/{}")]
    public void NormalizeRoute_produces_comparable_patterns(string input, string expected) =>
        Assert.Equal(expected, CrossLanguageEdgeResolver.NormalizeRoute(input));
}
