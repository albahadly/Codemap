using Codemap.Domain;

namespace Codemap.Application.Abstractions;

public sealed record SnapshotInfo(Guid Id, string ProjectName, DateTimeOffset ScannedAt, int NodeCount, int EdgeCount);

public interface IGraphRepository
{
    Task SaveAsync(TopologyGraph graph, CancellationToken ct = default);
    Task<TopologyGraph?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<SnapshotInfo>> GetHistoryAsync(string? projectName = null, CancellationToken ct = default);
}
