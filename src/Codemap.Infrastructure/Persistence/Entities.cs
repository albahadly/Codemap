namespace Codemap.Infrastructure.Persistence;

public class TopologySnapshotEntity
{
    public Guid Id { get; set; }
    public string ProjectName { get; set; } = default!;
    public DateTimeOffset ScannedAt { get; set; }
    public string GraphJson { get; set; } = default!;   // serialized TopologyGraph (nodes + edges)
}

public class ScanHistoryEntity
{
    public Guid Id { get; set; }
    public string Path { get; set; } = default!;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int NodeCount { get; set; }
    public int EdgeCount { get; set; }
    public string Status { get; set; } = default!;
}
