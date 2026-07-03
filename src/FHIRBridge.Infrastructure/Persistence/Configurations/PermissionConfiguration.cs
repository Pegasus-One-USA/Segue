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
            // Original platform permissions.
            PermissionSeed(SeededSecurityIds.ConfigurationWritePermissionId, UnifiedPermissions.ConfigurationWrite, "Manage source, destination, mapping, webhook, and route configuration.", "Configuration"),
            PermissionSeed(SeededSecurityIds.PipelineExecutePermissionId, UnifiedPermissions.PipelineExecute, "Execute configured pipeline routes.", "Pipeline"),
            PermissionSeed(SeededSecurityIds.AuditLogsReadPermissionId, UnifiedPermissions.AuditLogsRead, "Read operational audit logs.", "Audit"),
            PermissionSeed(SeededSecurityIds.SourceConnectionsTestPermissionId, UnifiedPermissions.SourceConnectionsTest, "Test source system connectivity.", "Configuration"),

            // User module permissions.
            PermissionSeed(SeededSecurityIds.UserInvitePermissionId, UnifiedPermissions.UserInvite, "Invite a new user to the organization.", "User"),
            PermissionSeed(SeededSecurityIds.UserViewPermissionId, UnifiedPermissions.UserView, "View the list of users.", "User"),
            PermissionSeed(SeededSecurityIds.UserEditPermissionId, UnifiedPermissions.UserEdit, "Update a user's profile information.", "User"),
            PermissionSeed(SeededSecurityIds.UserDeactivatePermissionId, UnifiedPermissions.UserDeactivate, "Deactivate a user account.", "User"),

            // Role module permissions.
            PermissionSeed(SeededSecurityIds.RoleCreatePermissionId, UnifiedPermissions.RoleCreate, "Create a new custom role.", "Role"),
            PermissionSeed(SeededSecurityIds.RoleEditPermissionId, UnifiedPermissions.RoleEdit, "Edit an existing role.", "Role"),
            PermissionSeed(SeededSecurityIds.RoleDeletePermissionId, UnifiedPermissions.RoleDelete, "Delete a custom role.", "Role"),
            PermissionSeed(SeededSecurityIds.RoleAssignPermissionId, UnifiedPermissions.RoleAssign, "Assign or remove roles from users.", "Role"),
            PermissionSeed(SeededSecurityIds.RoleViewPermissionId, UnifiedPermissions.RoleView, "View roles and their permissions.", "Role"),

            // Workflow module permissions.
            PermissionSeed(SeededSecurityIds.WorkflowCreatePermissionId, UnifiedPermissions.WorkflowCreate, "Create a new workflow.", "Workflow"),
            PermissionSeed(SeededSecurityIds.WorkflowEditPermissionId, UnifiedPermissions.WorkflowEdit, "Edit an existing workflow.", "Workflow"),
            PermissionSeed(SeededSecurityIds.WorkflowDeletePermissionId, UnifiedPermissions.WorkflowDelete, "Delete a workflow.", "Workflow"),
            PermissionSeed(SeededSecurityIds.WorkflowRunPermissionId, UnifiedPermissions.WorkflowRun, "Execute a workflow.", "Workflow"),
            PermissionSeed(SeededSecurityIds.WorkflowViewPermissionId, UnifiedPermissions.WorkflowView, "View workflow details.", "Workflow"),

            // Report / payload permissions.
            PermissionSeed(SeededSecurityIds.ReportViewPermissionId, UnifiedPermissions.ReportView, "View reports and analytics.", "Report"),
            PermissionSeed(SeededSecurityIds.PayloadViewPermissionId, UnifiedPermissions.PayloadView, "View data payloads from workflow runs.", "Payload"));
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
