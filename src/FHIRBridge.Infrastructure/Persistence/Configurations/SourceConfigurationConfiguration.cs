using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class SourceConfigurationConfiguration : IEntityTypeConfiguration<SourceConfiguration>
{
    // Compares the space-joined string[] columns by value so EF change-tracking treats reordered/rebuilt arrays correctly.
    private static readonly ValueComparer<string[]> StringArrayComparer = new(
        (left, right) => ReferenceEquals(left, right) || (left != null && right != null && left.SequenceEqual(right)),
        value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, StringComparer.Ordinal.GetHashCode(item))),
        value => value.ToArray());

    public void Configure(EntityTypeBuilder<SourceConfiguration> builder)
    {
        builder.ToTable("SourceConfigurations");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ConnectionId).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();

        builder.HasOne<SourceConnection>()
            .WithMany()
            .HasForeignKey(x => x.ConnectionId)
            .OnDelete(DeleteBehavior.Restrict);

        // See SourceConnectionConfiguration for why this column has no HasMaxLength: a Backend System workflow's
        // scope string carries one "system/{ResourceType}.rs" entry per selected resource type, and Epic's real-world
        // CapabilityStatement routinely advertises 50-100+ resource types.
        var scopesProperty = builder.Property(x => x.Scopes)
            .HasConversion(
                value => string.Join(' ', value),
                value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .HasColumnName("Scopes");
        scopesProperty.Metadata.SetValueComparer(StringArrayComparer);

        builder.OwnsOne(x => x.Retrieval, retrieval =>
        {
            retrieval.Property(x => x.RetrievalMethod)
                .HasMaxLength(50)
                .HasColumnName("RetrievalMethod");

            var resourceTypes = retrieval.Property(x => x.ResourceTypes)
                .HasConversion(
                    value => string.Join(' ', value),
                    value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .HasMaxLength(1000)
                .HasColumnName("RetrievalResourceTypes");
            resourceTypes.Metadata.SetValueComparer(StringArrayComparer);

            retrieval.Property(x => x.SearchCriteria)
                .HasMaxLength(1000)
                .HasColumnName("RetrievalSearchCriteria");

            retrieval.Property(x => x.IncrementalSyncEnabled)
                .HasColumnName("RetrievalIncrementalSyncEnabled");

            retrieval.Property(x => x.PageSize)
                .HasColumnName("RetrievalPageSize");

            retrieval.Property(x => x.SortOrder)
                .HasMaxLength(50)
                .HasColumnName("RetrievalSortOrder");

            var includeParameters = retrieval.Property(x => x.IncludeParameters)
                .HasConversion(
                    value => string.Join(' ', value),
                    value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .HasMaxLength(500)
                .HasColumnName("RetrievalIncludeParameters");
            includeParameters.Metadata.SetValueComparer(StringArrayComparer);

            var revIncludeParameters = retrieval.Property(x => x.RevIncludeParameters)
                .HasConversion(
                    value => string.Join(' ', value),
                    value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .HasMaxLength(500)
                .HasColumnName("RetrievalRevIncludeParameters");
            revIncludeParameters.Metadata.SetValueComparer(StringArrayComparer);

            retrieval.Property(x => x.RetryPolicy)
                .HasMaxLength(50)
                .HasColumnName("RetrievalRetryPolicy");

            retrieval.Property(x => x.TimeoutSeconds)
                .HasColumnName("RetrievalTimeoutSeconds");

            retrieval.Property(x => x.MaxRecordsPerRun)
                .HasColumnName("RetrievalMaxRecordsPerRun");

            retrieval.Property(x => x.LastSuccessfulSyncUtc)
                .HasColumnName("RetrievalLastSuccessfulSyncUtc");

            retrieval.Property(x => x.ExportScope)
                .HasMaxLength(50)
                .HasColumnName("RetrievalExportScope");

            retrieval.Property(x => x.GroupId)
                .HasMaxLength(200)
                .HasColumnName("RetrievalGroupId");

            var patientIds = retrieval.Property(x => x.PatientIds)
                .HasConversion(
                    value => string.Join(' ', value),
                    value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .HasMaxLength(4000)
                .HasColumnName("RetrievalPatientIds");
            patientIds.Metadata.SetValueComparer(StringArrayComparer);

            retrieval.Property(x => x.OutputFormat)
                .HasMaxLength(100)
                .HasColumnName("RetrievalOutputFormat");
        });
    }
}
