using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class AuthenticationLogConfiguration : IEntityTypeConfiguration<AuthenticationLog>
{
    public void Configure(EntityTypeBuilder<AuthenticationLog> builder)
    {
        builder.ToTable("AuthenticationLogs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserEmail).HasMaxLength(320);
        builder.Property(x => x.AuthenticationType).HasMaxLength(50).IsRequired();
        builder.Property(x => x.FailureReason).HasMaxLength(500);
        builder.Property(x => x.IpAddress).HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasMaxLength(500);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);

        builder.HasIndex(x => x.OccurredOnUtc);
        builder.HasIndex(x => x.UserEmail);
        builder.HasIndex(x => x.CorrelationId);
    }
}
