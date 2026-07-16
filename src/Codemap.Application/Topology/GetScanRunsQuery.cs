using Codemap.Application.Abstractions;
using Codemap.Application.Messaging;

namespace Codemap.Application.Topology;

/// <summary>Recent scan runs (status, timings, counts) for the history panel.</summary>
public sealed record GetScanRunsQuery(int Take = 50) : IRequest<IReadOnlyList<ScanRunInfo>>;

public sealed class GetScanRunsQueryHandler(IScanHistoryRepository history)
    : IRequestHandler<GetScanRunsQuery, IReadOnlyList<ScanRunInfo>>
{
    public Task<IReadOnlyList<ScanRunInfo>> Handle(GetScanRunsQuery request, CancellationToken ct = default) =>
        history.GetRecentAsync(request.Take, ct);
}
