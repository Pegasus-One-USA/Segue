using System.Text.Json;
using FHIRBridge.Runtime.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

// EF mappings for the Runtime DAG plane's pipeline run history (SqlPipelineRunStore). Mirrors the WorkflowRun/
// WorkflowNodeRun mappings in WorkflowPersistenceConfigurations.cs — same field-backed, constructor-bound domain
// shape, same "child collection is a field-access navigation, not a settable property" approach.

public sealed class PipelineRunEntityTypeConfiguration : IEntityTypeConfiguration<PipelineRun>
{
    public void Configure(EntityTypeBuilder<PipelineRun> builder)
    {
        builder.ToTable("PipelineRuns");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.SourceType).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(x => x.DestinationType).HasConversion<string>().HasMaxLength(50).IsRequired();

        // A get-only, constructor-bound IReadOnlyList<string> — JSON round-trip is simpler here than a separate
        // shadow-property column (see BulkExportJob's RequestedResourceTypesJson) since there's no settable
        // property to shadow.
        builder.Property(x => x.RequestedResourceTypes)
            .HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<string[]>(json, (JsonSerializerOptions?)null) ?? Array.Empty<string>())
            .IsRequired();

        builder.Property(x => x.TriggeredBy).HasMaxLength(200);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(x => x.ExtractedResourceCount).IsRequired();
        builder.Property(x => x.WrittenResourceCount).IsRequired();
        builder.Property(x => x.FailureMessage);
        builder.Property(x => x.ErrorReferenceId).HasMaxLength(50);
        builder.Property(x => x.StartedOnUtc).IsRequired();
        builder.Property(x => x.CompletedOnUtc);

        builder.HasMany(x => x.Steps)
            .WithOne()
            .HasForeignKey(step => step.PipelineRunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(PipelineRun.Steps))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(x => x.StartedOnUtc);
        builder.HasIndex(x => x.Status);
        builder.HasIndex(x => x.CorrelationId);
    }
}

public sealed class PipelineRunStepEntityTypeConfiguration : IEntityTypeConfiguration<PipelineRunStep>
{
    public void Configure(EntityTypeBuilder<PipelineRunStep> builder)
    {
        builder.ToTable("PipelineRunSteps");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.PipelineRunId).IsRequired();
        builder.Property(x => x.StepType).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(x => x.ResourceType).HasMaxLength(200);
        builder.Property(x => x.ResourceCount).IsRequired();
        builder.Property(x => x.Message);
        builder.Property(x => x.StartedOnUtc).IsRequired();
        builder.Property(x => x.CompletedOnUtc);

        builder.HasIndex(x => x.PipelineRunId);
    }
}

public sealed class PipelineRunEventEntityTypeConfiguration : IEntityTypeConfiguration<PipelineRunEvent>
{
    public void Configure(EntityTypeBuilder<PipelineRunEvent> builder)
    {
        builder.ToTable("PipelineRunEvents");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.PipelineRunId).IsRequired();
        builder.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.StepType).HasConversion<string>().HasMaxLength(50);
        builder.Property(x => x.ResourceType).HasMaxLength(200);
        builder.Property(x => x.ResourceId).HasMaxLength(256);
        builder.Property(x => x.Message).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(100);
        builder.Property(x => x.OccurredOnUtc).IsRequired();

        builder.HasIndex(x => x.PipelineRunId);
        builder.HasIndex(x => x.OccurredOnUtc);
    }
}
