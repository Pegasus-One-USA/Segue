using System.Text.Json;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class SourceConnectionConfiguration : IEntityTypeConfiguration<SourceConnection>
{
    // Compares the space-joined string[] columns by value so EF change-tracking treats reordered/rebuilt arrays correctly.
    private static readonly ValueComparer<string[]> StringArrayComparer = new(
        (left, right) => ReferenceEquals(left, right) || (left != null && right != null && left.SequenceEqual(right)),
        value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, StringComparer.Ordinal.GetHashCode(item))),
        value => value.ToArray());

    // Same as StringArrayComparer but null-safe, for the nullable DiscoveredScopes column (null until Discover has run).
    private static readonly ValueComparer<string[]?> NullableStringArrayComparer = new(
        (left, right) => ReferenceEquals(left, right) || (left != null && right != null && left.SequenceEqual(right)),
        value => value == null ? 0 : value.Aggregate(0, (hash, item) => HashCode.Combine(hash, StringComparer.Ordinal.GetHashCode(item))),
        value => value == null ? null : value.ToArray());

    // Compares the JSON-serialized resource-type -> last-sync-timestamp map by content, independent of key order.
    private static readonly ValueComparer<IReadOnlyDictionary<string, DateTime>> LastSuccessfulSyncMapComparer = new(
        (left, right) => left!.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(right!.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)),
        value => value.Aggregate(0, (hash, kv) => HashCode.Combine(hash, StringComparer.OrdinalIgnoreCase.GetHashCode(kv.Key), kv.Value)),
        value => value.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase));

    public void Configure(EntityTypeBuilder<SourceConnection> builder)
    {
        builder.ToTable("SourceConnections");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.SourceSystemType).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(x => x.BaseUrl).HasMaxLength(500).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();

        builder.Property(x => x.ApplicationType)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.OwnsOne(x => x.Interactive, interactive =>
        {
            var redirectUris = interactive.Property(x => x.RedirectUris)
                .HasConversion(
                    value => string.Join(' ', value),
                    value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .HasMaxLength(2000)
                .HasColumnName("RedirectUris");
            redirectUris.Metadata.SetValueComparer(StringArrayComparer);

            interactive.Property(x => x.LaunchUrl)
                .HasMaxLength(500)
                .HasColumnName("LaunchUrl");

            interactive.Property(x => x.PostLaunchRedirectUri)
                .HasMaxLength(500)
                .HasColumnName("PostLaunchRedirectUri");

            interactive.Property(x => x.PatientSelectionMethod)
                .HasConversion<string>()
                .HasMaxLength(50)
                .HasColumnName("PatientSelectionMethod");

            interactive.Property(x => x.LaunchDisplayMode)
                .HasConversion<string>()
                .HasMaxLength(50)
                .HasColumnName("LaunchDisplayMode");

            var trustedIssuers = interactive.Property(x => x.TrustedIssuers)
                .HasConversion(
                    value => string.Join(' ', value),
                    value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .HasMaxLength(2000)
                .HasColumnName("TrustedIssuers");
            trustedIssuers.Metadata.SetValueComparer(StringArrayComparer);
        });

        builder.OwnsOne(x => x.Authentication, authentication =>
        {
            authentication.Property(x => x.AuthenticationType)
                .HasConversion<string>()
                .HasMaxLength(100)
                .HasColumnName("AuthenticationType")
                .IsRequired();

            authentication.Property(x => x.ClientId)
                .HasMaxLength(300)
                .HasColumnName("ClientId");

            authentication.Property(x => x.TokenEndpoint)
                .HasMaxLength(500)
                .HasColumnName("TokenEndpoint");

            authentication.Property(x => x.KeyId)
                .HasMaxLength(200)
                .HasColumnName("KeyId");

            authentication.Property(x => x.JwksUrl)
                .HasMaxLength(500)
                .HasColumnName("JwksUrl");

            // No HasMaxLength: a Backend System source's scope string carries one "system/{ResourceType}.rs" entry
            // per selected resource type, and that selection can be seeded from live SMART discovery against the
            // real source's CapabilityStatement — which for Epic routinely advertises 50-100+ resource types, not
            // just this app's ~12-entry static default list. 1000 chars truncates real-world scope lists; there's
            // no app-level cap that would make any other finite length safe either, so this is nvarchar(max).
            var scopesProperty = authentication.Property(x => x.Scopes)
                .HasConversion(
                    value => string.Join(' ', value),
                    value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .HasColumnName("Scopes");

            scopesProperty.Metadata.SetValueComparer(new ValueComparer<string[]>(
                (left, right) => ReferenceEquals(left, right) || (left != null && right != null && left.SequenceEqual(right)),
                value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, StringComparer.Ordinal.GetHashCode(item))),
                value => value.ToArray()));

            // Null until the user clicks "Discover" against the backend-auth-scopes probe; holds whatever scope
            // string Epic's token response actually granted, distinct from Scopes (what we requested). Purely
            // informational — never read when building the real token request.
            var discoveredScopesProperty = authentication.Property(x => x.DiscoveredScopes)
                .HasConversion(
                    value => value == null ? null : string.Join(' ', value),
                    value => value == null ? null : value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .HasColumnName("DiscoveredScopes");
            discoveredScopesProperty.Metadata.SetValueComparer(NullableStringArrayComparer);

            authentication.OwnsOne(x => x.ClientSecret, secret =>
            {
                secret.Property(x => x.KeyVaultName)
                    .HasMaxLength(200)
                    .HasColumnName("ClientSecretKeyVaultName");
                secret.Property(x => x.SecretName)
                    .HasMaxLength(200)
                    .HasColumnName("ClientSecretName");
            });

            authentication.OwnsOne(x => x.PrivateKey, secret =>
            {
                secret.Property(x => x.KeyVaultName)
                    .HasMaxLength(200)
                    .HasColumnName("PrivateKeyKeyVaultName");
                secret.Property(x => x.SecretName)
                    .HasMaxLength(200)
                    .HasColumnName("PrivateKeySecretName");
            });
        });

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

            var lastSuccessfulSync = retrieval.Property(x => x.LastSuccessfulSyncUtcByResourceType)
                .HasConversion(
                    value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                    value => JsonSerializer.Deserialize<Dictionary<string, DateTime>>(value, (JsonSerializerOptions?)null)
                        ?? new Dictionary<string, DateTime>())
                .HasColumnName("RetrievalLastSuccessfulSyncByResourceType");
            lastSuccessfulSync.Metadata.SetValueComparer(LastSuccessfulSyncMapComparer);

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
