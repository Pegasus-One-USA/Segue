namespace FHIRBridge.Application.Abstractions.Licensing;

/// <summary>
/// Live counts of the four dimensions <see cref="LicenseLimits"/> caps, resolved from the product's real
/// repositories so the portal can show "how much of the license is actually being used" next to each
/// limit. Read-only: nothing here compares a count against a limit or blocks/gates anything on the result
/// — that is a later, separate enforcement stage.
/// </summary>
public interface ILicenseUsageCountsProvider
{
    Task<LicenseUsageCounts> GetCurrentCountsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Current live counts mirroring <see cref="LicenseLimits"/>' four dimensions. <see cref="WorkflowCount"/>
/// combines both pipeline-execution planes this product has — the Configured Pipeline's
/// <c>ResourcePipelineRoute</c> and the Runtime plane's <c>WorkflowDefinition</c> — into one number, since
/// a license's "workflows" term is meant to cover either.
/// </summary>
public sealed record LicenseUsageCounts(int UserCount, int SourceConnectionCount, int TenantCount, int WorkflowCount);
