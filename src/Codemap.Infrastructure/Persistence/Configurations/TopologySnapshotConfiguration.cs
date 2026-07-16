using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Codemap.Infrastructure.Persistence.Configurations;

public sealed class TopologySnapshotConfiguration : IEntityTypeConfiguration<TopologySnapshotEntity>
{
    public void Configure(EntityTypeBuilder<TopologySnapshotEntity> builder)
    {
        builder.ToTable("TopologySnapshots");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever(); // id comes from the scan, not the database
        builder.Property(s => s.ProjectName).HasMaxLength(256).IsRequired();
        builder.HasIndex(s => new { s.ProjectName, s.ScannedAt });
        builder.Property(s => s.GraphJson).HasColumnType("nvarchar(max)").IsRequired();
    }
}
