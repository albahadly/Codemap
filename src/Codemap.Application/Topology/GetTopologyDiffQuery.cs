using Codemap.Application.Abstractions;
using Codemap.Application.Messaging;
using Codemap.Domain;
using Codemap.Domain.Graph;

namespace Codemap.Application.Topology;

public sealed record GetTopologyDiffQuery(Guid FromSnapshotId, Guid ToSnapshotId) : IRequest<TopologyDiff>;

public sealed class GetTopologyDiffQueryHandler(IScanResultStore store, IGraphRepository repository)
    : IRequestHandler<GetTopologyDiffQuery, TopologyDiff>
{
    public async Task<TopologyDiff> Handle(GetTopologyDiffQuery request, CancellationToken ct = default)
    {
        var from = await LoadAsync(request.FromSnapshotId, ct).ConfigureAwait(false);
        var to = await LoadAsync(request.ToSnapshotId, ct).ConfigureAwait(false);
        return TopologyDiffer.Diff(from, to);
    }

    private async Task<TopologyGraph> LoadAsync(Guid id, CancellationToken ct) =>
        store.Get(id)
        ?? await repository.GetAsync(id, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Snapshot {id} was not found.");
}
