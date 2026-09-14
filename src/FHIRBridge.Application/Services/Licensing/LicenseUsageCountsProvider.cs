using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Runtime.Application.Workflows.Storage;

namespace FHIRBridge.Application.Services.Licensing;

/// <summary>
/// Composes existing repositories only (no new EF/DbContext code) to report the live counts behind each
/// <see cref="LicenseLimits"/> dimension. Called once per portal page load, so reading full lists and
/// taking <c>.Count</c> where a repository has no cheap dedicated count method is an acceptable cost here.
/// </summary>
public sealed class LicenseUsageCountsProvider : ILicenseUsageCountsProvider
{
    private readonly IUserAccessRepository _userAccessRepository;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ITenantRepository _tenantRepository;
    private readonly IWorkflowDefinitionStore _workflowDefinitionStore;

    public LicenseUsageCountsProvider(
        IUserAccessRepository userAccessRepository,
        IConfigurationRepository configurationRepository,
        ITenantRepository tenantRepository,
        IWorkflowDefinitionStore workflowDefinitionStore)
    {
        _userAccessRepository = userAccessRepository;
        _configurationRepository = configurationRepository;
        _tenantRepository = tenantRepository;
        _workflowDefinitionStore = workflowDefinitionStore;
    }

    public async Task<LicenseUsageCounts> GetCurrentCountsAsync(CancellationToken cancellationToken)
    {
        var users = await _userAccessRepository.GetUsersAsync(cancellationToken);
        var sourceConnections = await _configurationRepository.GetSourceConnectionsAsync(cancellationToken);
        var routes = await _configurationRepository.GetRoutesAsync(cancellationToken);
        var tenants = await _tenantRepository.GetAllAsync(cancellationToken);
        var workflowDefinitions = await _workflowDefinitionStore.ListAsync(cancellationToken);

        // "Workflows" spans both pipeline-execution planes: the Configured Pipeline's routes and the
        // Runtime plane's workflow definitions are combined into one number for display.
        var workflowCount = routes.Count + workflowDefinitions.Count;

        return new LicenseUsageCounts(users.Count, sourceConnections.Count, tenants.Count, workflowCount);
    }
}
