using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Codemap.Infrastructure.Persistence.Configurations;

public sealed class ScanHistoryConfiguration : IEntityTypeConfiguration<ScanHistoryEntity>
{
    public void Configure(EntityTypeBuilder<ScanHistoryEntity> builder)
    {
        builder.ToTable("ScanHistory");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Path).HasMaxLength(1024).IsRequired();
        builder.Property(s => s.Status).HasMaxLength(512).IsRequired();
        builder.HasIndex(s => s.StartedAt);
    }
}
