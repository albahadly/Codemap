namespace Codemap.Application.Abstractions;

public sealed record ScanRunInfo(
    Guid Id,
    string Path,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int NodeCount,
    int EdgeCount,
    string Status);

public interface IScanHistoryRepository
{
    Task RecordStartAsync(Guid scanId, string path, DateTimeOffset startedAt, CancellationToken ct = default);
    Task RecordCompletionAsync(Guid scanId, int nodeCount, int edgeCount, string status, CancellationToken ct = default);
    Task<IReadOnlyList<ScanRunInfo>> GetRecentAsync(int take = 50, CancellationToken ct = default);
}
