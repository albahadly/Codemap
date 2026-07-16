using Microsoft.EntityFrameworkCore;

namespace Codemap.Infrastructure.Persistence;

public sealed class CodemapDbContext(DbContextOptions<CodemapDbContext> options) : DbContext(options)
{
    public DbSet<TopologySnapshotEntity> Snapshots => Set<TopologySnapshotEntity>();
    public DbSet<ScanHistoryEntity> ScanHistory => Set<ScanHistoryEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CodemapDbContext).Assembly);
}
