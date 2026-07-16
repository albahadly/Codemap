namespace Codemap.Domain;

public sealed record CodeEdge(string FromId, string ToId, EdgeKind Kind, string? Detail); // Detail e.g. "POST /api/scan"
