using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class ErrorLogConfiguration : IEntityTypeConfiguration<ErrorLog>
{
    public void Configure(EntityTypeBuilder<ErrorLog> builder)
    {
        builder.ToTable("ErrorLogs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Severity).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ExceptionType).HasMaxLength(200).IsRequired();
        // Unbounded: the Errors screen's "What happened" text and its technical details must show the whole
        // message/stack trace, so these are stored in full rather than clipped to a fixed width.
        builder.Property(x => x.Message).IsRequired();
        builder.Property(x => x.StackTrace);
        builder.Property(x => x.Module).HasMaxLength(100);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);

        // ── Phase 6A – Enterprise Global Exception Management ────────────────────
        builder.Property(x => x.ErrorReferenceId).HasMaxLength(40);
        builder.Property(x => x.Category).HasMaxLength(40);
        builder.Property(x => x.UserFriendlyMessage);
        builder.Property(x => x.ExecutionId).HasMaxLength(100);
        builder.Property(x => x.WorkflowId).HasMaxLength(100);
        builder.Property(x => x.EndpointId).HasMaxLength(200);
        builder.Property(x => x.RequestId).HasMaxLength(100);
        builder.Property(x => x.TraceId).HasMaxLength(64);
        builder.Property(x => x.SpanId).HasMaxLength(32);
        builder.Property(x => x.DiagnosisAction).HasMaxLength(20);
        builder.Property(x => x.DiagnosisCause).HasMaxLength(500);

        builder.HasIndex(x => x.OccurredOnUtc);
        builder.HasIndex(x => x.CorrelationId);
        builder.HasIndex(x => x.Severity);
        builder.HasIndex(x => x.ExecutionId);
        builder.HasIndex(x => x.Category);
        // Filtered unique index: new rows always carry a reference id; pre-existing rows (null) are exempt.
        builder.HasIndex(x => x.ErrorReferenceId)
            .IsUnique()
            .HasFilter("[ErrorReferenceId] IS NOT NULL");
    }
}
