using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// HIPAA #2: reads the per-destination <c>DeIdentificationProfileId</c> and requires de-identification under
/// that profile when set. Never denies access on its own — this rule only ever contributes a profile id, so
/// it changes behavior only for destinations a tenant admin has explicitly assigned a profile to.
/// </summary>
public sealed class DestinationSensitivityGovernanceRule : IGovernanceRule
{
    public const string PolicyName = "DestinationSensitivity";

    private readonly FHIRBridgeDbContext _dbContext;

    public DestinationSensitivityGovernanceRule(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<GovernanceRuleResult> EvaluateAsync(
        ResourceGovernanceContext context,
        CancellationToken cancellationToken)
    {
        if (context.DestinationConfigurationId is not { } destinationId)
        {
            return GovernanceRuleResult.Allow(PolicyName);
        }

        var deIdentificationProfileId = await _dbContext.DestinationConfigurations
            .AsNoTracking()
            .Where(x => x.Id == destinationId)
            .Select(x => x.DeIdentificationProfileId)
            .FirstOrDefaultAsync(cancellationToken);

        return GovernanceRuleResult.Allow(PolicyName, deIdentificationProfileId);
    }
}
