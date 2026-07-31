using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class BulkExportJobConfiguration : IEntityTypeConfiguration<BulkExportJob>
{
    public void Configure(EntityTypeBuilder<BulkExportJob> builder)
    {
        builder.ToTable("BulkExportJobs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.SourcePath).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();
        builder.Property(x => x.SourceConnectionId).IsRequired();
        builder.Property(x => x.ExportRequestJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.StatusUrl).HasMaxLength(2000);
        builder.Property(x => x.KickedOffOnUtc).IsRequired();
        builder.Property(x => x.ErrorMessage).HasColumnType("nvarchar(max)");
        builder.Property(x => x.CorrelationId).HasMaxLength(100);
        builder.Property(x => x.TriggeredBy).HasMaxLength(200);
        builder.Property(x => x.PriorNodeOutputsJson).HasColumnType("nvarchar(max)");
        builder.Property(x => x.ContextJson).HasColumnType("nvarchar(max)");

        // Backs BulkExportPollWorker's due-job query.
        builder.HasIndex(x => new { x.Status, x.NextPollNotBeforeUtc });
        builder.HasIndex(x => x.WorkflowRunId);
        builder.HasIndex(x => x.CorrelationId);
    }
}
