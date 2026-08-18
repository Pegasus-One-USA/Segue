using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// HIPAA #2: reads the per-destination <c>RequiresDeIdentification</c> flag and requires de-identification when
/// set. Never denies access on its own — this rule only ever contributes a de-identification requirement, so it
/// changes behavior only for destinations a tenant admin has explicitly opted in.
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

        var requiresDeIdentification = await _dbContext.DestinationConfigurations
            .AsNoTracking()
            .Where(x => x.Id == destinationId)
            .Select(x => x.RequiresDeIdentification)
            .FirstOrDefaultAsync(cancellationToken);

        return GovernanceRuleResult.Allow(PolicyName, requiresDeIdentification);
    }
}
