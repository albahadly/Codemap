namespace Codemap.Domain;

public sealed record TopologyGraph(
    Guid Id,
    string ProjectName,
    DateTimeOffset ScannedAt,
    IReadOnlyList<CodeNode> Nodes,
    IReadOnlyList<CodeEdge> Edges);
