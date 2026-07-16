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
        builder.Property(x => x.Category).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Activity).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();
        builder.Property(x => x.EntityName).HasMaxLength(200);
        builder.Property(x => x.IpAddress).HasMaxLength(50);
        builder.Property(x => x.UserAgent).HasMaxLength(500);
        builder.Property(x => x.HttpMethod).HasMaxLength(10);
        builder.Property(x => x.RequestPath).HasMaxLength(500);
        builder.Property(x => x.Details).HasMaxLength(2000);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);
        builder.Property(x => x.SessionId).HasMaxLength(100);
        builder.Property(x => x.FailureReason).HasMaxLength(500);
        builder.Property(x => x.Severity).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Module).HasMaxLength(100);
        builder.Property(x => x.Action).HasMaxLength(100);
        builder.Property(x => x.OldValue).HasMaxLength(4000);
        builder.Property(x => x.NewValue).HasMaxLength(4000);
        builder.Property(x => x.PreviousHash).HasMaxLength(128);
        builder.Property(x => x.EntryHash).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredOnUtc).IsRequired();

        builder.HasIndex(x => x.UserId);
        builder.HasIndex(x => x.OccurredOnUtc);
        builder.HasIndex(x => x.Module);
    }
}
