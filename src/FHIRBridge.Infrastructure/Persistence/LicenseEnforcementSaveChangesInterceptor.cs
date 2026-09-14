using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>
/// Central license-quota/allow-list enforcement choke point — mirrors <see cref="AuditingSaveChangesInterceptor"/>'s
/// exact shape (a <see cref="SaveChangesInterceptor"/> registered once on <see cref="FHIRBridgeDbContext"/> that
/// walks <c>ChangeTracker.Entries()</c> on every <c>SaveChangesAsync</c> call) so that adding a new license-gated
/// creation rule in the future means editing exactly ONE method here — one more <c>case</c> branch below — never
/// another scattered call site in a service or controller.
///
/// Watches for a newly-<see cref="EntityState.Added"/> row of one of five entity types this product's license caps
/// or restricts (<see cref="User"/>, <see cref="SourceConnection"/>, <see cref="ResourcePipelineRoute"/>,
/// <see cref="WorkflowDefinition"/>, <see cref="DestinationConfiguration"/>) and, only for those, delegates the
/// actual count/allow-list decision to <see cref="ILicenseQuotaGuard"/> (which already implements this product's
/// fail-open policy — no license, an unlimited dimension, or the guard's own internal error all pass; only a
/// genuine cap/allow-list violation throws). Throwing here aborts the save before anything commits, exactly like
/// the append-only guard in <see cref="AuditingSaveChangesInterceptor"/> already does for a different rule.
///
/// Performance: the overwhelming majority of <c>SaveChangesAsync</c> calls touch none of these five types, so the
/// common case is a cheap in-memory scan over already-loaded <c>ChangeTracker.Entries()</c> with ZERO database
/// query — the same shape the existing audit interceptor already uses. A query only ever runs when a watched row
/// is genuinely being added (an admin creating a user/connection/route/destination/workflow — inherently rare,
/// never the data-processing hot path); the one dimension that COULD be hit on every pipeline run (the monthly
/// processed-records cap) is deliberately NOT enforced here at all — see <see cref="ILicenseQuotaGuard.EnsureCanStartNewRunAsync"/>'s
/// own remarks for why that check lives at the run-trigger call sites instead, with its own short-TTL cache.
/// </summary>
public sealed class LicenseEnforcementSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<LicenseEnforcementSaveChangesInterceptor> _logger;

    // Deliberately NOT constructor-injecting ILicenseQuotaGuard directly: its dependency chain
    // (ILicenseUsageCountsProvider -> IConfigurationRepository/IUserAccessRepository/etc.) resolves down to
    // FHIRBridgeDbContext itself in the real-DB DI branch. This interceptor is built INSIDE
    // AddDbContext<FHIRBridgeDbContext>'s own options factory (see DependencyInjection.cs), so injecting the
    // guard there directly means "building FHIRBridgeDbContext" requires "building FHIRBridgeDbContext" again
    // in the same resolution — a circular/reentrant DI resolution that hung the app at startup rather than
    // throwing a clean error. Resolving the guard lazily, from a fresh IServiceScopeFactory-created scope, at
    // SavingChangesAsync time (not at DbContext-construction time) breaks that cycle entirely: this
    // interceptor's own construction only ever needs the always-safe, dependency-free IServiceScopeFactory.
    public LicenseEnforcementSaveChangesInterceptor(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<LicenseEnforcementSaveChangesInterceptor> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    // Async-only, same rationale as AuditingSaveChangesInterceptor.AppendConfigAuditLogsAsync: every call site in
    // this codebase already uses SaveChangesAsync, and the license checks below need to run real (async) queries,
    // which has no sync-safe story here without blocking.
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is FHIRBridgeDbContext context)
        {
            await EnforceAsync(context, cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private async Task EnforceAsync(FHIRBridgeDbContext context, CancellationToken cancellationToken)
    {
        var newUserCount = 0;
        List<SourceConnection>? newSourceConnections = null;
        List<ResourcePipelineRoute>? newRoutes = null;
        var newWorkflowDefinitionCreateCount = 0;
        List<DestinationConfiguration>? newDestinations = null;

        // Pass 1: cheap in-memory scan only — no DB access. Matches AuditingSaveChangesInterceptor's own shape.
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added)
            {
                continue;
            }

            switch (entry.Entity)
            {
                case User:
                    newUserCount++;
                    break;

                case SourceConnection sourceConnection:
                    (newSourceConnections ??= []).Add(sourceConnection);
                    break;

                case ResourcePipelineRoute route:
                    (newRoutes ??= []).Add(route);
                    break;

                // SqlWorkflowDefinitionStore.SaveAsync always deletes-then-re-adds, even for an ordinary edit of
                // an existing workflow (across two separate SaveChangesAsync calls — the delete already committed
                // by the time this Add's own SaveChangesAsync runs), so a bare "this is an Added WorkflowDefinition"
                // check cannot tell a genuine create apart from a same-id edit. NextWorkflowDefinitionAddIsGenuineCreate
                // is that store's own explicit signal for exactly this — see its remarks on FHIRBridgeDbContext.
                case WorkflowDefinition when context.NextWorkflowDefinitionAddIsGenuineCreate:
                    newWorkflowDefinitionCreateCount++;
                    break;

                case DestinationConfiguration destination:
                    (newDestinations ??= []).Add(destination);
                    break;
            }
        }

        // Reset immediately after reading — SqlWorkflowDefinitionStore already resets this itself right after its
        // own save, but resetting here too means a future caller that ever adds a WorkflowDefinition some other
        // way can never accidentally inherit a stale "true" from an earlier, unrelated save on this same context.
        context.NextWorkflowDefinitionAddIsGenuineCreate = false;

        if (newUserCount == 0 && newSourceConnections is null && newRoutes is null
            && newWorkflowDefinitionCreateCount == 0 && newDestinations is null)
        {
            return;
        }

        // Pass 2: only reached when a watched row is genuinely being added — rare, admin-triggered config
        // changes, never the pipeline data-processing hot path. Resolved from a FRESH scope (see this class's
        // constructor remarks for why) rather than a constructor-injected instance — one extra scope/DbContext
        // per rare admin action is a negligible cost, and it's what makes this safe to build at all.
        using var scope = _serviceScopeFactory.CreateScope();
        var licenseQuotaGuard = scope.ServiceProvider.GetRequiredService<ILicenseQuotaGuard>();

        // Each call independently fail-opens on its own internal error (see ILicenseQuotaGuard's fail-open
        // policy) and throws only on a genuine cap/allow-list violation, aborting this SaveChangesAsync before
        // anything commits.
        for (var i = 0; i < newUserCount; i++)
        {
            await licenseQuotaGuard.EnsureUserQuotaAvailableAsync(cancellationToken);
        }

        if (newSourceConnections is not null)
        {
            foreach (var sourceConnection in newSourceConnections)
            {
                await licenseQuotaGuard.EnsureSourceConnectionQuotaAvailableAsync(
                    sourceConnection.SourceSystemType, sourceConnection.BaseUrl, cancellationToken);
            }
        }

        // "Workflows" is one combined quota dimension spanning both pipeline-execution planes (Configured
        // Pipeline routes + Runtime-plane workflow definitions) — see LicenseUsageCounts.WorkflowCount — so both
        // contribute to the same EnsureWorkflowQuotaAvailableAsync check.
        var workflowQuotaChecksNeeded = (newRoutes?.Count ?? 0) + newWorkflowDefinitionCreateCount;
        for (var i = 0; i < workflowQuotaChecksNeeded; i++)
        {
            await licenseQuotaGuard.EnsureWorkflowQuotaAvailableAsync(cancellationToken);
        }

        if (newRoutes is not null)
        {
            foreach (var route in newRoutes)
            {
                // A route's resource type is owned by its mapping profile (single source of truth — see
                // ConfigurationService's own remarks), which was already persisted in an earlier, separate
                // SaveChangesAsync call by the time a route referencing it is created, so a normal query
                // resolves it correctly. Same context instance mid-SaveChangesAsync is safe to query
                // sequentially — AuditingSaveChangesInterceptor.AppendConfigAuditLogsAsync already does exactly
                // this for AuditLog's previous-hash lookup.
                var mappingProfile = await context.Set<MappingProfile>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(mp => mp.Id == route.MappingProfileId, cancellationToken);

                if (mappingProfile is not null)
                {
                    await licenseQuotaGuard.EnsureResourceTypeAllowedAsync(mappingProfile.ResourceType, cancellationToken);
                }
            }
        }

        if (newDestinations is not null)
        {
            foreach (var destination in newDestinations)
            {
                await licenseQuotaGuard.EnsureDestinationTypeAllowedAsync(destination.DestinationType, cancellationToken);
            }
        }
    }
}
