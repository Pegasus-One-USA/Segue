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

        // Encrypted PHI payloads — ciphertext (base64) is longer than the source JSON, hence nvarchar(max).
        builder.Property(x => x.FetchedJson).IsRequired();
        builder.Property(x => x.NormalizedJson);
        builder.Property(x => x.MappedValuesJson);

        builder.Property(x => x.AppliedProfiles);
        builder.Property(x => x.Warnings);
        builder.Property(x => x.MasterPatientId).HasMaxLength(200);
        builder.Property(x => x.WriteStatus).HasMaxLength(50);
        builder.Property(x => x.FetchedAtUtc).IsRequired();

        builder.HasIndex(x => x.RouteExecutionId);
        builder.HasIndex(x => x.FetchedAtUtc);
    }
}
