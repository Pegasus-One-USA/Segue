namespace FHIRBridge.LicenseServer.Domain;

/// <summary>
/// Audit-trail record of one license token minted through this tool. Stores what was issued — never the
/// signing private key, and not even a second copy of anything secret — so an admin can look back at every
/// license this server has ever produced.
/// </summary>
public sealed class IssuedLicense
{
    public Guid Id { get; set; }

    public required string CustomerId { get; set; }

    public required string CustomerName { get; set; }

    public required string Edition { get; set; }

    public DateTime IssuedAtUtc { get; set; }

    public DateTime ExpiresUtc { get; set; }

    public int MaxUsers { get; set; }

    public int MaxWorkflows { get; set; }

    public int MaxSourceConnections { get; set; }

    public int MaxProcessedRecordsPerMonth { get; set; }

    public int MaxSuccessfulWorkflowExecutionsPerMonth { get; set; }

    /// <summary>Minutes after <see cref="IssuedAtUtc"/> this license had to be applied within, or null if
    /// it was minted with no activation deadline. Recorded for the audit trail — the actual enforcement
    /// deadline lives only in the token's own <c>activateByUtc</c> claim.</summary>
    public int? ActivationWindowMinutes { get; set; }

    /// <summary>Comma-joined list, for display in the audit table without re-parsing JSON.</summary>
    public string? AllowedSourceTypesSummary { get; set; }

    /// <summary>Comma-joined list, for display in the audit table without re-parsing JSON.</summary>
    public string? FeaturesSummary { get; set; }

    /// <summary>Comma-joined list, for display in the audit table without re-parsing JSON.</summary>
    public string? AllowedResourceTypesSummary { get; set; }

    /// <summary>Comma-joined list, for display in the audit table without re-parsing JSON.</summary>
    public string? AllowedDestinationTypesSummary { get; set; }

    /// <summary>Copied from the linked <see cref="LicenseRequest.RequestHost"/> at mint time, if this
    /// license was minted against a request (see <c>Pages/Licenses/Create</c>) — null for a license minted
    /// without a linked request, or one minted before this field existed. Denormalized here (rather than
    /// looked up via <see cref="LicenseRequest.FulfilledIssuedLicenseId"/>) so the audit trail shows which
    /// deployment asked for a license even if that request row is later removed.</summary>
    public string? RequestHost { get; set; }

    /// <summary>The exact JSON claim set that was signed into the token — the full audit fidelity record
    /// the spec asked for, beyond just the summary columns above.</summary>
    public required string ClaimsJson { get; set; }

    /// <summary>The signed compact-JWS token itself. Not a secret (it's the artifact handed to the
    /// customer) — kept so an admin can retrieve a previously minted license without re-signing it.</summary>
    public required string Token { get; set; }
}
