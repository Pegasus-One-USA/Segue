using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class AuthorizationLogConfiguration : IEntityTypeConfiguration<AuthorizationLog>
{
    public void Configure(EntityTypeBuilder<AuthorizationLog> builder)
    {
        builder.ToTable("AuthorizationLogs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserEmail).HasMaxLength(320);
        builder.Property(x => x.RequestPath).HasMaxLength(500).IsRequired();
        builder.Property(x => x.PermissionCode).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Result).HasMaxLength(20).IsRequired();
        builder.Property(x => x.IpAddress).HasMaxLength(64);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);

        builder.HasIndex(x => x.OccurredOnUtc);
        builder.HasIndex(x => x.UserEmail);
        builder.HasIndex(x => x.CorrelationId);
    }
}
