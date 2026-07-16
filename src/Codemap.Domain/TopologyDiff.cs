namespace Codemap.Domain;

public sealed record TopologyDiff(
    IReadOnlyList<CodeNode> Added,
    IReadOnlyList<CodeNode> Removed,
    IReadOnlyList<(CodeNode Before, CodeNode After)> Changed,
    IReadOnlyList<CodeEdge> EdgesAdded,
    IReadOnlyList<CodeEdge> EdgesRemoved);
