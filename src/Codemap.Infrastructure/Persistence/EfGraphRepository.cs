using Codemap.Application.Abstractions;
using Codemap.Domain;
using Microsoft.EntityFrameworkCore;

namespace Codemap.Infrastructure.Persistence;

/// <summary>
/// Snapshot persistence. Uses IDbContextFactory (not an injected long-lived context) because scans and
/// publishes can run outside a scoped request. Node/edge counts for the history list come from joining
/// ScanHistory rows (same id as the graph) — the snapshot table stays exactly the spec'd four columns.
/// </summary>
public sealed class EfGraphRepository(IDbContextFactory<CodemapDbContext> contextFactory) : IGraphRepository
{
    public async Task SaveAsync(TopologyGraph graph, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var json = GraphJsonSerializer.Serialize(graph);

        var existing = await db.Snapshots.FindAsync([graph.Id], ct).ConfigureAwait(false);
        if (existing is null)
        {
            db.Snapshots.Add(new TopologySnapshotEntity
            {
                Id = graph.Id,
                ProjectName = graph.ProjectName,
                ScannedAt = graph.ScannedAt,
                GraphJson = json,
            });
        }
        else
        {
            existing.ProjectName = graph.ProjectName;
            existing.ScannedAt = graph.ScannedAt;
            existing.GraphJson = json;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<TopologyGraph?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Snapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
        return entity is null ? null : GraphJsonSerializer.Deserialize(entity.GraphJson);
    }

    public async Task<IReadOnlyList<SnapshotInfo>> GetHistoryAsync(string? projectName = null, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.Snapshots.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(projectName))
            query = query.Where(s => s.ProjectName == projectName);

        var snapshots = await query
            .OrderByDescending(s => s.ScannedAt)
            .Select(s => new { s.Id, s.ProjectName, s.ScannedAt })
            .ToListAsync(ct).ConfigureAwait(false);

        var ids = snapshots.Select(s => s.Id).ToList();
        var counts = await db.ScanHistory.AsNoTracking()
            .Where(h => ids.Contains(h.Id))
            .Select(h => new { h.Id, h.NodeCount, h.EdgeCount })
            .ToDictionaryAsync(h => h.Id, ct).ConfigureAwait(false);

        return snapshots
            .Select(s => new SnapshotInfo(
                s.Id,
                s.ProjectName,
                s.ScannedAt,
                counts.TryGetValue(s.Id, out var c) ? c.NodeCount : 0,
                counts.TryGetValue(s.Id, out var c2) ? c2.EdgeCount : 0))
            .ToList();
    }
}
