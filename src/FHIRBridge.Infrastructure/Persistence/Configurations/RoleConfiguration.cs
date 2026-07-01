using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.Description)
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.IsSystem).IsRequired();
        builder.Property(x => x.IsDefault).IsRequired();

        builder.HasIndex(x => x.Name)
            .IsUnique();

        builder.HasData(
            RoleSeed(SeededSecurityIds.GlobalAdminRoleId, UnifiedRoles.GlobalAdmin, "Full platform administrator across all tenants."),
            RoleSeed(SeededSecurityIds.TenantAdminRoleId, UnifiedRoles.TenantAdmin, "Administers configuration and users within a tenant."),
            RoleSeed(SeededSecurityIds.PipelineEngineerRoleId, UnifiedRoles.PipelineEngineer, "Builds and runs pipeline configurations within a tenant."),
            RoleSeed(SeededSecurityIds.AnalystRoleId, UnifiedRoles.Analyst, "Runs pipelines and reviews data and audit output."),
            RoleSeed(SeededSecurityIds.AuditorRoleId, UnifiedRoles.Auditor, "Read-only access to configuration and audit logs."));
    }

    private static object RoleSeed(Guid id, string name, string description)
    {
        return new
        {
            Id = id,
            Name = name,
            Description = description,
            IsSystem = true,
            IsDefault = false,
            IsEnabled = true,
            IsDeleted = false,
            CreatedOnUtc = SeedConstants.SeedTimestamp
        };
    }
}
