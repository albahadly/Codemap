using Codemap.Application.Abstractions;
using Codemap.Application.Messaging;

namespace Codemap.Application.Topology;

/// <summary>Published snapshots (for the history/diff pickers); optionally filtered to one project.</summary>
public sealed record GetSnapshotsQuery(string? ProjectName = null) : IRequest<IReadOnlyList<SnapshotInfo>>;

public sealed class GetSnapshotsQueryHandler(IGraphRepository repository)
    : IRequestHandler<GetSnapshotsQuery, IReadOnlyList<SnapshotInfo>>
{
    public Task<IReadOnlyList<SnapshotInfo>> Handle(GetSnapshotsQuery request, CancellationToken ct = default) =>
        repository.GetHistoryAsync(request.ProjectName, ct);
}
