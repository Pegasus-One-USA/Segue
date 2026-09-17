using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class PipelineRunResourceRecordConfiguration : IEntityTypeConfiguration<PipelineRunResourceRecord>
{
    public void Configure(EntityTypeBuilder<PipelineRunResourceRecord> builder)
    {
        builder.ToTable("PipelineRunResourceRecords");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ResourceType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.SourceResourceId).HasMaxLength(200);
        builder.Property(x => x.Stage).HasMaxLength(50).IsRequired();
        builder.Property(x => x.ErrorMessage).HasMaxLength(2000);

        // The fetched/normalized/mapped JSON columns were removed — they held whole FHIR resources and mapped
        // field values, which is PHI whether or not it is encrypted at rest. This record now tracks the STAGE
        // each resource reached and how it scored, not what it contained.

        builder.Property(x => x.AppliedProfiles);
        builder.Property(x => x.Warnings);
        builder.Property(x => x.MasterPatientId).HasMaxLength(200);
        builder.Property(x => x.WriteStatus).HasMaxLength(50);
        builder.Property(x => x.FetchedAtUtc).IsRequired();

        builder.HasIndex(x => x.RouteExecutionId);
        builder.HasIndex(x => x.FetchedAtUtc);
    }
}
