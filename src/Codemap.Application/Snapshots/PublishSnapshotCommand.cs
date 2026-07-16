using Codemap.Application.Abstractions;
using Codemap.Application.Messaging;

namespace Codemap.Application.Snapshots;

public sealed record PublishSnapshotCommand(Guid GraphId) : IRequest<Guid>;

public sealed class PublishSnapshotCommandHandler(IScanResultStore store, IGraphRepository repository)
    : IRequestHandler<PublishSnapshotCommand, Guid>
{
    public async Task<Guid> Handle(PublishSnapshotCommand request, CancellationToken ct = default)
    {
        var graph = store.Get(request.GraphId)
            ?? throw new InvalidOperationException($"No unpublished scan result with id {request.GraphId} is available.");
        await repository.SaveAsync(graph, ct).ConfigureAwait(false);
        return graph.Id;
    }
}
