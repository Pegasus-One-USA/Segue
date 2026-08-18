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

    private readonly FHIRBridgeDbContext _db;

    public DeIdentificationProfileSeeder(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        var alreadySeeded = await _db.DeIdentificationProfiles
            .AnyAsync(x => x.Name == DefaultProfileName, cancellationToken);
        if (alreadySeeded)
        {
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
