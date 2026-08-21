using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class UserFhirContextBindingConfiguration : IEntityTypeConfiguration<UserFhirContextBinding>
{
    public void Configure(EntityTypeBuilder<UserFhirContextBinding> builder)
    {
        builder.ToTable("UserFhirContextBindings");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserIdentity).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ResourceType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.ResourceId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.CallerIdentity).HasMaxLength(200);
        builder.Property(x => x.CallerFhirUserId).HasMaxLength(200);

        // One binding per (source connection, end user) — the invariant CompleteAsync enforces.
        builder.HasIndex(x => new { x.SourceConnectionId, x.UserIdentity }).IsUnique();
    }
}
