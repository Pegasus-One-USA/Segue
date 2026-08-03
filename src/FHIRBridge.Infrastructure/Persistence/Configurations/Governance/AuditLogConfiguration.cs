using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.SequenceNumber).ValueGeneratedOnAdd().UseIdentityColumn();
        builder.Property(x => x.Actor).HasMaxLength(320).IsRequired();
        builder.Property(x => x.Module).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Action).HasMaxLength(100).IsRequired();
        builder.Property(x => x.EntityType).HasMaxLength(200);
        builder.Property(x => x.EntityId).HasMaxLength(256);
        builder.Property(x => x.EntityName).HasMaxLength(200);
        builder.Property(x => x.OldValueJson).HasMaxLength(4000);
        builder.Property(x => x.NewValueJson).HasMaxLength(4000);
        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Remarks).HasMaxLength(1000);
        builder.Property(x => x.IpAddress).HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasMaxLength(500);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);
        builder.Property(x => x.PreviousHash).HasMaxLength(128);
        builder.Property(x => x.EntryHash).HasMaxLength(128).IsRequired();

        builder.HasIndex(x => x.SequenceNumber).IsUnique();
        builder.HasIndex(x => x.OccurredOnUtc);
        builder.HasIndex(x => x.CorrelationId);
        builder.HasIndex(x => new { x.EntityType, x.EntityId });
    }
}
