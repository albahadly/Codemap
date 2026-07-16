using Codemap.Domain;

namespace Codemap.Application.Abstractions;

/// <summary>
/// Holds scan results in memory until the user publishes them as persistent snapshots
/// (PublishSnapshotCommand). Assumption: the spec separates "scan" from "publish snapshot",
/// so fresh scans live here and only published graphs reach SQL Server.
/// </summary>
public interface IScanResultStore
{
    void Add(TopologyGraph graph);
    TopologyGraph? Get(Guid id);
    IReadOnlyList<TopologyGraph> GetAll();
}
