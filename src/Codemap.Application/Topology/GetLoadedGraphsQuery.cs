using Codemap.Application.Abstractions;
using Codemap.Application.Messaging;

namespace Codemap.Application.Topology;

/// <summary>
/// Summaries of unpublished in-memory scan results, so the UI can re-list them after a circuit
/// reconnect without re-scanning.
/// </summary>
public sealed record GetLoadedGraphsQuery : IRequest<IReadOnlyList<SnapshotInfo>>;

public sealed class GetLoadedGraphsQueryHandler(IScanResultStore store)
    : IRequestHandler<GetLoadedGraphsQuery, IReadOnlyList<SnapshotInfo>>
{
    public Task<IReadOnlyList<SnapshotInfo>> Handle(GetLoadedGraphsQuery request, CancellationToken ct = default)
    {
        IReadOnlyList<SnapshotInfo> result = store.GetAll()
            .OrderByDescending(g => g.ScannedAt)
            .Select(g => new SnapshotInfo(g.Id, g.ProjectName, g.ScannedAt, g.Nodes.Count, g.Edges.Count))
            .ToList();
        return Task.FromResult(result);
    }
}
