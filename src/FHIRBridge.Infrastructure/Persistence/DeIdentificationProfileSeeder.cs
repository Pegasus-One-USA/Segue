using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>Seeds the "HIPAA Safe Harbor — Default" profile the first time the app starts against a fresh
/// database — ports the rules that used to live in <c>SafeHarborDeIdentificationService.DefaultSafeHarborRules</c>
/// before de-identification became profile-based. Insert-only: a profile with this name already existing (an
/// admin's own, or from a prior run) means this has already happened, so it no-ops.</summary>
public sealed class DeIdentificationProfileSeeder : IDeIdentificationProfileSeeder
{
    public const string DefaultProfileName = "HIPAA Safe Harbor — Default";

    /// <summary>Durable "this has already run" marker. Deliberately NOT inferred from the profile or its rules
    /// still existing: an admin who deletes the seeded defaults means it, and inferring from live data re-armed
    /// the seeder the moment they finished cleaning up — so the rules came back on the next restart, looking
    /// like they had reappeared on their own. A setting row survives deleting both.</summary>
    public const string SeededSettingKey = "DeIdentification:DefaultProfileSeeded";

    private readonly FHIRBridgeDbContext _db;
    private readonly ISystemSettingRepository _settings;

    public DeIdentificationProfileSeeder(FHIRBridgeDbContext db, ISystemSettingRepository settings)
    {
        _db = db;
        _settings = settings;
    }

    public async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        // Three independent "already done" signals, checked cheapest-first. The marker is what makes deleting
        // the defaults stick; the other two keep an existing installation (seeded before the marker existed)
        // from being re-seeded on the upgrade that introduces it.
        if (await _settings.GetByKeyAsync(SeededSettingKey, cancellationToken) is not null)
        {
            return;
        }

        var profileExists = await _db.DeIdentificationProfiles
            .AnyAsync(x => x.Name == DefaultProfileName, cancellationToken);
        var seededRulesExist = await _db.TransformationRules
            .AnyAsync(x => x.DeIdentificationProfileId == DeIdentificationProfile.DefaultProfileId, cancellationToken);
        if (profileExists || seededRulesExist)
        {
            // Record the marker so the check above short-circuits from now on, and so deleting these defaults
            // later is honoured instead of undone by the next restart.
            await _settings.UpsertAsync(
                SeededSettingKey, "true", "Set once the default de-identification profile has been seeded.",
                cancellationToken);
            return;
        }

        var profile = new DeIdentificationProfile(
            DefaultProfileName,
            "HIPAA Safe Harbor defaults, ported from the platform's original hardcoded rule list.",
            id: DeIdentificationProfile.DefaultProfileId);
        await _db.DeIdentificationProfiles.AddAsync(profile, cancellationToken);

        foreach (var (scope, resourceType, sourceField, mode) in DefaultRules)
        {
            var configJson = $$"""{"mode":"{{mode}}"}""";
            var rule = new TransformationRule(
                scope,
                TransformNodeType.HashingMasking,
                configJson,
                resourceType: resourceType,
                sourceField: sourceField,
                executionPhase: TransformExecutionPhase.PreMapping,
                deIdentificationProfileId: profile.Id);
            await _db.TransformationRules.AddAsync(rule, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);

        await _settings.UpsertAsync(
            SeededSettingKey, "true", "Set once the default de-identification profile has been seeded.",
            cancellationToken);
    }

    private static readonly (TransformScope Scope, string? ResourceType, string SourceField, string Mode)[] DefaultRules =
    [
        (TransformScope.ResourceType, "Patient", "name", "remove"),
        (TransformScope.ResourceType, "Patient", "telecom", "remove"),
        (TransformScope.ResourceType, "Patient", "photo", "remove"),
        (TransformScope.ResourceType, "Patient", "contact", "remove"),
        (TransformScope.ResourceType, "Patient", "address.line", "remove"),
        (TransformScope.ResourceType, "Patient", "address.text", "remove"),
        (TransformScope.ResourceType, "Patient", "address.postalCode", "generalizeZip3"),
        (TransformScope.ResourceType, "Patient", "birthDate", "generalizeDateToYear"),
        (TransformScope.ResourceType, "Patient", "identifier.value", "hash"),
        (TransformScope.ResourceType, "Provenance", "agent.who.display", "remove"),
        (TransformScope.ResourceType, "Provenance", "target.display", "remove"),
        (TransformScope.Global, null, "text", "remove"),
    ];
}
