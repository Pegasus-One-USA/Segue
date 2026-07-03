using FHIRBridge.Application.Abstractions.Governance;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Configuration-driven retention policy. Resolves the retention period for a resource type from
/// <c>Governance:Retention</c>, falling back from a per-resource-type override → platform default (7 years).
/// Replaces the hard-coded policy so retention is configurable per resource type.
/// </summary>
public sealed class ConfiguredRetentionPolicyService : IRetentionPolicyService
{
    private const int FallbackYears = 7;

    private readonly IConfiguration _configuration;

    public ConfiguredRetentionPolicyService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public RetentionPolicy GetPolicy(string resourceType)
    {
        var section = _configuration.GetSection("Governance:Retention");

        var years = ReadInt(section.GetSection($"ResourceTypes:{resourceType}:Years"))
            ?? ReadInt(section.GetSection("DefaultYears"))
            ?? FallbackYears;

        var immutableAudit = !bool.TryParse(section["ImmutableAudit"], out var parsed) || parsed;

        return new RetentionPolicy(
            RetentionYears: years,
            IsImmutableAuditRequired: immutableAudit,
            RequiresPhiFreeAudit: true);
    }

    private static int? ReadInt(IConfigurationSection section)
        => int.TryParse(section.Value, out var value) && value > 0 ? value : null;
}
