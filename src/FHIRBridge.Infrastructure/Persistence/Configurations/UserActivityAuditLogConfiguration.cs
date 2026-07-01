using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class UserActivityAuditLogConfiguration : IEntityTypeConfiguration<UserActivityAuditLog>
{
    public void Configure(EntityTypeBuilder<UserActivityAuditLog> builder)
    {
        builder.ToTable("UserActivityAuditLogs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserEmail).HasMaxLength(320).IsRequired();
        builder.Property(x => x.Category).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Activity).HasMaxLength(150).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();
        builder.Property(x => x.EntityName).HasMaxLength(150);
        builder.Property(x => x.IpAddress).HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasMaxLength(512);
        builder.Property(x => x.HttpMethod).HasMaxLength(16);
        builder.Property(x => x.RequestPath).HasMaxLength(1024);
        builder.Property(x => x.Details).HasMaxLength(4000);
        builder.Property(x => x.CorrelationId).HasMaxLength(128);
        builder.Property(x => x.SessionId).HasMaxLength(128);
        builder.Property(x => x.FailureReason).HasMaxLength(1024);
        builder.Property(x => x.Severity).HasMaxLength(20).IsRequired();
        builder.Property(x => x.PreviousHash).HasMaxLength(64);
        builder.Property(x => x.EntryHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.OccurredOnUtc).IsRequired();

        builder.HasIndex(x => new { x.TenantId, x.OccurredOnUtc });
        builder.HasIndex(x => x.UserId);
        builder.HasIndex(x => x.Activity);
    }
}
