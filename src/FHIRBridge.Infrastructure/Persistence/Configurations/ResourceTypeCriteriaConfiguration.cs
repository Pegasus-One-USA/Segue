using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class ResourceTypeCriteriaConfiguration : IEntityTypeConfiguration<ResourceTypeCriteria>
{
    public void Configure(EntityTypeBuilder<ResourceTypeCriteria> builder)
    {
        builder.ToTable("ResourceTypeCriteria");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.WorkflowId).IsRequired();
        builder.Property(x => x.SourceNodeId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ResourceType).HasMaxLength(100).IsRequired();

        // Raw FHIR search parameters. Generous but bounded: a criteria string is a handful of parameters, while
        // an identifier/_id list scoping a cohort can legitimately run long.
        builder.Property(x => x.Criteria).HasMaxLength(4000).IsRequired();

        // One criteria row per resource type per source node per workflow — the invariant behind the single
        // input box each resource type gets. Filtered so soft-deleted rows don't block re-adding criteria for
        // the same resource type. The T-SQL bracket quoting and 0/1 boolean literal are overridden for Npgsql
        // in FHIRBridgeDbContext.OnModelCreating, alongside the other filtered indexes.
        builder.HasIndex(x => new { x.WorkflowId, x.SourceNodeId, x.ResourceType })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0");

        // Lookup path the extraction hits once per run: every criteria row for the workflow being executed.
        builder.HasIndex(x => x.WorkflowId);
    }
}
