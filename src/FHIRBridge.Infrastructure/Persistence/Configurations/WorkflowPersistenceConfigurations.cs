using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

// EF mappings for the ranked-workflow graph engine (Scenario A: durable graphs + run history).
// The domain aggregates are immutable, constructor-bound, and expose their child collections through
// read-only navigations backed by private fields — so every relationship is configured for field access
// and there is no inverse navigation on the child side (the FK scalar carries the relationship).

public sealed class WorkflowDefinitionEntityTypeConfiguration : IEntityTypeConfiguration<WorkflowDefinition>
{
    public void Configure(EntityTypeBuilder<WorkflowDefinition> builder)
    {
        builder.ToTable("WorkflowDefinitions");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(400).IsRequired();
        builder.Property(x => x.Version).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.IsPubliclyLaunchable).IsRequired().HasDefaultValue(false);

        // Computed convenience alias over IsEnabled — not a stored column.
        builder.Ignore(x => x.IsActive);

        builder.Property(x => x.LastTriggeredOnUtc);

        // Required with a DB-level default so this is never null even for a row inserted outside the normal
        // SqlWorkflowDefinitionStore.SaveAsync path (that path itself always stamps a real actor/timestamp —
        // see its own remarks — these defaults are strictly a safety net, not the primary source of truth).
        builder.Property(x => x.CreatedOnUtc).IsRequired().HasDefaultValueSql("GETUTCDATE()");
        builder.Property(x => x.CreatedBy).IsRequired().HasMaxLength(320).HasDefaultValue("system");
        builder.Property(x => x.UpdatedOnUtc);
        builder.Property(x => x.UpdatedBy).HasMaxLength(320);

        // Workflow-level scheduling metadata (approach B) — flattened into the WorkflowDefinitions row. Optional:
        // existing rows (and manual/launched workflows) simply have null trigger columns.
        builder.OwnsOne(x => x.Trigger, trigger =>
        {
            trigger.Property(t => t.Type).HasConversion<string>().HasMaxLength(50).HasColumnName("TriggerType");
            trigger.Property(t => t.ScheduleExpression).HasMaxLength(200).HasColumnName("TriggerScheduleExpression");
            trigger.Property(t => t.IntervalMinutes).HasColumnName("TriggerIntervalMinutes");
            trigger.Property(t => t.BackfillOnFirstRun).HasColumnName("TriggerBackfillOnFirstRun");
        });
        builder.Navigation(x => x.Trigger).IsRequired(false);

        builder.HasMany(x => x.Nodes)
            .WithOne()
            .HasForeignKey(node => node.WorkflowDefinitionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.Edges)
            .WithOne()
            .HasForeignKey(edge => edge.WorkflowDefinitionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(WorkflowDefinition.Nodes))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
        builder.Metadata.FindNavigation(nameof(WorkflowDefinition.Edges))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class WorkflowNodeEntityTypeConfiguration : IEntityTypeConfiguration<WorkflowNode>
{
    public void Configure(EntityTypeBuilder<WorkflowNode> builder)
    {
        builder.ToTable("WorkflowNodes");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.WorkflowDefinitionId).IsRequired();
        builder.Property(x => x.NodeType).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Category).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(x => x.Rank).IsRequired();
        builder.Property(x => x.SubRank).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(400).IsRequired();
        builder.Property(x => x.ConfigurationJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.PositionX).IsRequired();
        builder.Property(x => x.PositionY).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.CheckpointUrlEnabled).IsRequired().HasDefaultValue(false);

        builder.HasMany(x => x.Configuration)
            .WithOne()
            .HasForeignKey(configuration => configuration.WorkflowNodeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(WorkflowNode.Configuration))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(x => x.WorkflowDefinitionId);
    }
}

public sealed class WorkflowNodeConfigurationEntityTypeConfiguration
    : IEntityTypeConfiguration<WorkflowNodeConfiguration>
{
    public void Configure(EntityTypeBuilder<WorkflowNodeConfiguration> builder)
    {
        builder.ToTable("WorkflowNodeConfigurations");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.WorkflowNodeId).IsRequired();
        builder.Property(x => x.Key).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Value).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.IsSecret).IsRequired();

        builder.HasIndex(x => x.WorkflowNodeId);
    }
}

public sealed class WorkflowEdgeEntityTypeConfiguration : IEntityTypeConfiguration<WorkflowEdge>
{
    public void Configure(EntityTypeBuilder<WorkflowEdge> builder)
    {
        builder.ToTable("WorkflowEdges");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.WorkflowDefinitionId).IsRequired();
        builder.Property(x => x.FromNodeId).IsRequired();
        builder.Property(x => x.ToNodeId).IsRequired();

        builder.HasIndex(x => x.WorkflowDefinitionId);
    }
}

public sealed class WorkflowRunEntityTypeConfiguration : IEntityTypeConfiguration<WorkflowRun>
{
    public void Configure(EntityTypeBuilder<WorkflowRun> builder)
    {
        builder.ToTable("WorkflowRuns");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.WorkflowDefinitionId).IsRequired();
        builder.Property(x => x.WorkflowDefinitionVersion).IsRequired();
        builder.Property(x => x.StartedAt).IsRequired();
        builder.Property(x => x.CompletedAt);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(x => x.ErrorMessage).HasColumnType("nvarchar(max)");
        builder.Property(x => x.TriggeredBy).HasMaxLength(200);
        builder.Property(x => x.TriggerType).HasMaxLength(50);
        builder.Property(x => x.TargetNodeId);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);

        builder.HasMany(x => x.NodeRuns)
            .WithOne()
            .HasForeignKey(nodeRun => nodeRun.WorkflowRunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(WorkflowRun.NodeRuns))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(x => x.WorkflowDefinitionId);
        builder.HasIndex(x => x.StartedAt);
        builder.HasIndex(x => x.CorrelationId);
    }
}

public sealed class WorkflowNodeRunPayloadEntityTypeConfiguration : IEntityTypeConfiguration<WorkflowNodeRunPayload>
{
    public void Configure(EntityTypeBuilder<WorkflowNodeRunPayload> builder)
    {
        builder.ToTable("WorkflowNodeRunPayloads");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.WorkflowRunId).IsRequired();
        builder.Property(x => x.WorkflowNodeRunId).IsRequired();
        builder.Property(x => x.NodeType).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Contract).HasMaxLength(100).IsRequired();
        // Encrypted at rest (can carry PHI: raw fetched resources, mapped field values) — see EfWorkflowNodeResourceHistoryRecorder.
        builder.Property(x => x.PayloadJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.ItemCount);
        builder.Property(x => x.RecordedAtUtc).IsRequired();

        builder.HasIndex(x => x.WorkflowRunId);
        builder.HasIndex(x => x.RecordedAtUtc);
    }
}

public sealed class WorkflowNodeRunEntityTypeConfiguration : IEntityTypeConfiguration<WorkflowNodeRun>
{
    public void Configure(EntityTypeBuilder<WorkflowNodeRun> builder)
    {
        builder.ToTable("WorkflowNodeRuns");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.WorkflowRunId).IsRequired();
        builder.Property(x => x.WorkflowNodeId).IsRequired();
        builder.Property(x => x.NodeType).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Rank).IsRequired();
        builder.Property(x => x.SubRank).IsRequired();
        builder.Property(x => x.StartedAt).IsRequired();
        builder.Property(x => x.CompletedAt);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(x => x.ErrorMessage).HasColumnType("nvarchar(max)");
        builder.Property(x => x.LineageJson).HasColumnType("nvarchar(max)");

        builder.HasIndex(x => x.WorkflowRunId);
    }
}
