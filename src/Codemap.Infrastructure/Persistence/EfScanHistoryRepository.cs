using Codemap.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Codemap.Infrastructure.Persistence;

public sealed class EfScanHistoryRepository(IDbContextFactory<CodemapDbContext> contextFactory) : IScanHistoryRepository
{
    public async Task RecordStartAsync(Guid scanId, string path, DateTimeOffset startedAt, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.ScanHistory.Add(new ScanHistoryEntity
        {
            Id = scanId,
            Path = path.Length > 1024 ? path[..1024] : path,
            StartedAt = startedAt,
            Status = "Running",
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordCompletionAsync(Guid scanId, int nodeCount, int edgeCount, string status, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.ScanHistory.FindAsync([scanId], ct).ConfigureAwait(false);
        if (entity is null) return; // start record may be missing if the DB was unreachable at scan start
        entity.CompletedAt = DateTimeOffset.UtcNow;
        entity.NodeCount = nodeCount;
        entity.EdgeCount = edgeCount;
        entity.Status = status.Length > 512 ? status[..512] : status;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScanRunInfo>> GetRecentAsync(int take = 50, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ScanHistory.AsNoTracking()
            .OrderByDescending(h => h.StartedAt)
            .Take(take)
            .Select(h => new ScanRunInfo(h.Id, h.Path, h.StartedAt, h.CompletedAt, h.NodeCount, h.EdgeCount, h.Status))
            .ToListAsync(ct).ConfigureAwait(false);
    }
}
