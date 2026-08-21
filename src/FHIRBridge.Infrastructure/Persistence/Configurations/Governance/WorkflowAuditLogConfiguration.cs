using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class WorkflowAuditLogConfiguration : IEntityTypeConfiguration<WorkflowAuditLog>
{
    public void Configure(EntityTypeBuilder<WorkflowAuditLog> builder)
    {
        builder.ToTable("WorkflowAuditLogs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.NodeType).HasMaxLength(200);
        builder.Property(x => x.ResourceType).HasMaxLength(200);
        builder.Property(x => x.ResourceIdHash).HasMaxLength(128);
        builder.Property(x => x.InputContract).HasMaxLength(100).IsRequired();
        builder.Property(x => x.OutputContract).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Message).HasMaxLength(2000);

        builder.HasIndex(x => x.WorkflowRunId);
        builder.HasIndex(x => x.OccurredOnUtc);
    }
}
