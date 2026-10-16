using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class CdsTrustedIssuerConfiguration : IEntityTypeConfiguration<CdsTrustedIssuer>
{
    public void Configure(EntityTypeBuilder<CdsTrustedIssuer> builder)
    {
        builder.ToTable("CdsTrustedIssuers");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(CdsTrustedIssuer.NameMaxLength).IsRequired();
        builder.Property(x => x.Vendor).HasMaxLength(CdsTrustedIssuer.VendorMaxLength).IsRequired();
        builder.Property(x => x.Issuer).HasMaxLength(CdsTrustedIssuer.IssuerMaxLength).IsRequired();
        builder.Property(x => x.JwksUrl).HasMaxLength(CdsTrustedIssuer.UrlMaxLength);
        builder.Property(x => x.JwksJson);
        builder.Property(x => x.Audience).HasMaxLength(CdsTrustedIssuer.IssuerMaxLength);
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.ExpectedSubject).HasMaxLength(CdsTrustedIssuer.SubjectMaxLength);
        builder.Property(x => x.FhirBaseUrl).HasMaxLength(CdsTrustedIssuer.UrlMaxLength);
        builder.Property(x => x.AllowCallback).IsRequired();
        builder.Ignore(x => x.EffectiveFhirBaseUrl);

        // One registration per iss among live rows — two would make a call's key set ambiguous. Filtered so a
        // soft-deleted row does not block re-adding the same issuer (PostgreSQL filter text: FHIRBridgeDbContext).
        builder.HasIndex(x => x.Issuer).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}

public sealed class CdsServiceInstanceConfiguration : IEntityTypeConfiguration<CdsServiceInstance>
{
    public void Configure(EntityTypeBuilder<CdsServiceInstance> builder)
    {
        builder.ToTable("CdsServiceInstances");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ServiceId).HasMaxLength(CdsServiceInstance.ServiceIdMaxLength).IsRequired();
        builder.Property(x => x.Title).HasMaxLength(CdsServiceInstance.TitleMaxLength).IsRequired();
        builder.Property(x => x.HandlerKey).HasMaxLength(CdsServiceInstance.HandlerKeyMaxLength).IsRequired();
        builder.Property(x => x.Hook).HasMaxLength(CdsServiceInstance.HookMaxLength).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(CdsServiceInstance.DescriptionMaxLength).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.SettingsJson).IsRequired();
        builder.Property(x => x.CardSourceLabel).HasMaxLength(CdsServiceInstance.CardSourceLabelMaxLength).IsRequired();
        builder.Property(x => x.CardSourceUrl).HasMaxLength(CdsServiceInstance.UrlMaxLength);
        builder.Property(x => x.FetchMissingData).IsRequired();
        builder.Property(x => x.FixesJson);
        builder.Property(x => x.OverrideReasonsJson);
        builder.Property(x => x.LinksJson);
        builder.Ignore(x => x.FixesEnabled);
        builder.Property(x => x.SignedOffBy).HasMaxLength(CdsServiceInstance.SignedOffByMaxLength);
        builder.Property(x => x.SignedOffAtUtc);
        builder.Property(x => x.SignOffNote).HasMaxLength(CdsServiceInstance.SignOffNoteMaxLength);
        builder.Ignore(x => x.IsClinicallySignedOff);
        builder.Ignore(x => x.ContentVersion);

        // A primitive collection: a JSON array column on SQL Server, a uuid[] column on PostgreSQL.
        builder.PrimitiveCollection(x => x.AllowedIssuerIds).IsRequired();

        // The URL slug is unique among live rows (PostgreSQL filter text: FHIRBridgeDbContext).
        builder.HasIndex(x => x.ServiceId).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}

public sealed class CdsCallLogConfiguration : IEntityTypeConfiguration<CdsCallLog>
{
    public void Configure(EntityTypeBuilder<CdsCallLog> builder)
    {
        builder.ToTable("CdsCallLogs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.ServiceId).HasMaxLength(CdsCallLog.ServiceIdMaxLength).IsRequired();
        builder.Property(x => x.HandlerKey).HasMaxLength(CdsCallLog.HandlerKeyMaxLength);
        builder.Property(x => x.Hook).HasMaxLength(CdsCallLog.HookMaxLength);
        builder.Property(x => x.HookInstance).HasMaxLength(CdsCallLog.HookInstanceMaxLength);
        builder.Property(x => x.Issuer).HasMaxLength(CdsCallLog.IssuerMaxLength);
        builder.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.Detail).HasMaxLength(CdsCallLog.DetailMaxLength);
        builder.Property(x => x.TriggerAction).HasMaxLength(CdsCallLog.TriggerActionMaxLength);
        builder.Property(x => x.EpicVersion).HasMaxLength(CdsCallLog.VersionMaxLength);
        builder.Property(x => x.CriteriaId).HasMaxLength(CdsCallLog.CriteriaIdMaxLength);
        builder.Property(x => x.ImplementationVersion).HasMaxLength(CdsCallLog.VersionMaxLength);
        builder.Property(x => x.SuggestionsOffered).IsRequired();
        builder.Property(x => x.SuggestionsAccepted).IsRequired();
        builder.Property(x => x.OverrideReasonCodes).HasMaxLength(CdsCallLog.OverrideReasonCodesMaxLength);
        builder.Property(x => x.SystemActionCount).IsRequired();
        builder.Property(x => x.PayerId);
        builder.Property(x => x.PayerName).HasMaxLength(CdsCallLog.PayerNameMaxLength);
        builder.Property(x => x.CoverageDecisions).HasMaxLength(CdsCallLog.CoverageDecisionsMaxLength);
        builder.Property(x => x.RelayUsed);
        builder.Property(x => x.RelayMs);
        builder.Property(x => x.RelayOutcome).HasMaxLength(CdsCallLog.RelayOutcomeMaxLength);
        builder.Property(x => x.CoverageAssertionCount);

        builder.HasIndex(x => x.OccurredOnUtc);
        builder.HasIndex(x => new { x.ServiceId, x.OccurredOnUtc });
        builder.HasIndex(x => x.Outcome);
    }
}

public sealed class CdsPayerConfiguration : IEntityTypeConfiguration<CdsPayer>
{
    public void Configure(EntityTypeBuilder<CdsPayer> builder)
    {
        builder.ToTable("CdsPayers");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(CdsPayer.NameMaxLength).IsRequired();
        builder.PrimitiveCollection(x => x.Identifiers).IsRequired();
        builder.PrimitiveCollection(x => x.PlanValues).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.RelayEnabled).IsRequired();
        builder.Property(x => x.RelayServiceUrl).HasMaxLength(CdsPayer.UrlMaxLength);
        builder.Property(x => x.RelayTimeoutMs).IsRequired();
        builder.Property(x => x.FallbackToLocalRules).IsRequired();
        builder.Property(x => x.ForwardFhirAuthorization).IsRequired();
        builder.Property(x => x.RelayClientId).HasMaxLength(CdsPayer.ClientIdMaxLength);
        builder.Ignore(x => x.UsesRelay);

        // One payer per name among live rows (PostgreSQL filter text: FHIRBridgeDbContext).
        builder.HasIndex(x => x.Name).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}

public sealed class CdsCoverageRuleConfiguration : IEntityTypeConfiguration<CdsCoverageRule>
{
    public void Configure(EntityTypeBuilder<CdsCoverageRule> builder)
    {
        builder.ToTable("CdsCoverageRules");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.PayerId).IsRequired();
        builder.Property(x => x.System).HasMaxLength(CdsCoverageRule.SystemMaxLength).IsRequired();
        builder.Property(x => x.Code).HasMaxLength(CdsCoverageRule.CodeMaxLength).IsRequired();
        builder.Property(x => x.CodePrefixMatch).IsRequired();
        builder.Property(x => x.Covered).HasMaxLength(30).IsRequired();
        builder.Property(x => x.PaNeeded).HasMaxLength(30);
        builder.Property(x => x.DocNeeded).HasMaxLength(30);
        builder.PrimitiveCollection(x => x.DocPurposes).IsRequired();
        builder.PrimitiveCollection(x => x.InfoNeeded).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(CdsCoverageRule.ReasonMaxLength);
        builder.Property(x => x.PolicyUrl).HasMaxLength(CdsCoverageRule.UrlMaxLength);
        builder.Property(x => x.QuestionnaireCanonical).HasMaxLength(CdsCoverageRule.UrlMaxLength);
        builder.Property(x => x.EffectiveFrom).IsRequired();
        builder.Property(x => x.EffectiveTo);
        builder.Property(x => x.IsEnabled).IsRequired();

        // No navigation to the payer: payers and rules are soft-deleted together by the payer service.
        builder.HasIndex(x => new { x.PayerId, x.System, x.Code, x.EffectiveFrom }).IsUnique().HasFilter("[IsDeleted] = 0");
        builder.HasIndex(x => x.Code);
    }
}
