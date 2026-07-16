using System.Text.Json;
using System.Text.Json.Serialization;
using Codemap.Domain;

namespace Codemap.Infrastructure.Persistence;

/// <summary>Round-trips a <see cref="TopologyGraph"/> to the GraphJson column. Enums stored as strings
/// so snapshots stay readable/diffable even if enum ordinals shift between versions.</summary>
public static class GraphJsonSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(TopologyGraph graph) => JsonSerializer.Serialize(graph, Options);

    public static TopologyGraph Deserialize(string json) =>
        JsonSerializer.Deserialize<TopologyGraph>(json, Options)
        ?? throw new InvalidOperationException("Snapshot GraphJson deserialized to null.");
}
