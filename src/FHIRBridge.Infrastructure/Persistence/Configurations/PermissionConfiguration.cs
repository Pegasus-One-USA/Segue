using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Permissions");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name)
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(x => x.Description)
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(x => x.Category).HasMaxLength(100);
        builder.Property(x => x.IsSystem).IsRequired();

        builder.HasIndex(x => x.Name)
            .IsUnique();

        builder.HasData(
            PermissionSeed(SeededSecurityIds.TenantsReadPermissionId, UnifiedPermissions.TenantsRead, "Read tenant configuration.", "Tenancy"),
            PermissionSeed(SeededSecurityIds.TenantsWritePermissionId, UnifiedPermissions.TenantsWrite, "Create and update tenants.", "Tenancy"),
            PermissionSeed(SeededSecurityIds.ConfigurationWritePermissionId, UnifiedPermissions.ConfigurationWrite, "Manage source, destination, mapping, webhook, and route configuration.", "Configuration"),
            PermissionSeed(SeededSecurityIds.PipelineExecutePermissionId, UnifiedPermissions.PipelineExecute, "Execute configured pipeline routes.", "Pipeline"),
            PermissionSeed(SeededSecurityIds.AuditLogsReadPermissionId, UnifiedPermissions.AuditLogsRead, "Read operational audit logs.", "Audit"),
            PermissionSeed(SeededSecurityIds.SourceConnectionsTestPermissionId, UnifiedPermissions.SourceConnectionsTest, "Test source system connectivity.", "Configuration"));
    }

    private static object PermissionSeed(Guid id, string name, string description, string category)
    {
        return new
        {
            Id = id,
            Name = name,
            Description = description,
            Category = category,
            IsSystem = true,
            IsDeleted = false,
            CreatedOnUtc = SeedConstants.SeedTimestamp
        };
    }
}
