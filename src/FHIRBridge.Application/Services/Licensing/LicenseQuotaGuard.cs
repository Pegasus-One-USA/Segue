using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Application.Services.Licensing;

/// <summary>
/// Real <see cref="ILicenseQuotaGuard"/> implementation. Every method resolves the license status and any
/// live counts it needs INSIDE a try/catch that logs-and-passes on any internal failure (fail-open — this is a
/// commercial boundary, not a security boundary); the actual "throw and block" decision always happens AFTER
/// that try/catch has completed successfully, so a deliberate rejection is never accidentally swallowed by the
/// same catch that exists to protect against our own bugs.
/// </summary>
public sealed class LicenseQuotaGuard : ILicenseQuotaGuard
{
    private const string ExecutionStatsCacheKey = "LicenseQuotaGuard:ExecutionStats";

    // A monthly quota doesn't need per-request precision, and this check sits on the pipeline-run TRIGGER path
    // (scheduled routes, webhooks — can fire frequently), so a live COUNT/SUM query on every single trigger
    // would be a real perf cost for no real accuracy benefit. A short TTL keeps the number fresh enough while
    // making the overwhelmingly common case (well within cap) a cheap in-memory read. Both run-trigger checks
    // (processed records, successful executions) share ONE cached stats object/TTL rather than querying twice.
    private static readonly TimeSpan ExecutionStatsCacheTtl = TimeSpan.FromSeconds(45);

    private readonly ILicenseService _licenseService;
    private readonly ILicenseUsageCountsProvider _usageCountsProvider;
    private readonly ILicenseUsageExecutionStatsProvider _executionStatsProvider;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<LicenseQuotaGuard> _logger;

    public LicenseQuotaGuard(
        ILicenseService licenseService,
        ILicenseUsageCountsProvider usageCountsProvider,
        ILicenseUsageExecutionStatsProvider executionStatsProvider,
        IMemoryCache memoryCache,
        ILogger<LicenseQuotaGuard> logger)
    {
        _licenseService = licenseService;
        _usageCountsProvider = usageCountsProvider;
        _executionStatsProvider = executionStatsProvider;
        _memoryCache = memoryCache;
        _logger = logger;
    }

    public async Task EnsureUserQuotaAvailableAsync(CancellationToken cancellationToken)
    {
        (int Limit, int CurrentCount)? overLimit = null;

        try
        {
            var limits = _licenseService.Current.Limits;
            if (limits is null || limits.MaxUsers == LicenseLimits.Unlimited)
            {
                return;
            }

            var counts = await _usageCountsProvider.GetCurrentCountsAsync(cancellationToken);
            if (counts.UserCount >= limits.MaxUsers)
            {
                overLimit = (limits.MaxUsers, counts.UserCount);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "License quota guard failed while checking the user quota; failing open.");
            return;
        }

        if (overLimit is { } value)
        {
            throw new LicenseQuotaExceededException("users", value.Limit, value.CurrentCount);
        }
    }

    public async Task EnsureSourceConnectionQuotaAvailableAsync(
        SourceSystemType vendorType, string baseUrl, CancellationToken cancellationToken)
    {
        (int Limit, int CurrentCount)? overLimit = null;
        (string Reason, string Message)? disallowed = null;

        try
        {
            var limits = _licenseService.Current.Limits;
            if (limits is null)
            {
                return;
            }

            disallowed = CheckSourceAllowList(limits, vendorType, baseUrl);

            if (disallowed is null && limits.MaxSourceConnections != LicenseLimits.Unlimited)
            {
                var counts = await _usageCountsProvider.GetCurrentCountsAsync(cancellationToken);
                if (counts.SourceConnectionCount >= limits.MaxSourceConnections)
                {
                    overLimit = (limits.MaxSourceConnections, counts.SourceConnectionCount);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "License quota guard failed while checking the source connection quota; failing open.");
            return;
        }

        if (disallowed is { } value1)
        {
            throw new LicenseRestrictionViolationException(value1.Reason, value1.Message);
        }

        if (overLimit is { } value2)
        {
            throw new LicenseQuotaExceededException("source connections", value2.Limit, value2.CurrentCount);
        }
    }

    /// <summary>
    /// Re-validates a source connection's vendor type and hospital allow-list at the moment it's actually
    /// USED — i.e. at pipeline/workflow run-trigger time, for every distinct source connection that run will
    /// touch — not just once at the connection's own creation time. Deliberately does NOT re-check
    /// <see cref="LicenseLimits.MaxSourceConnections"/> (that's a create-time row-count cap; it has no
    /// meaning applied to a connection that already exists and is simply being used again). This closes the
    /// gap where a connection created while allowed could otherwise keep running forever even after its
    /// hospital/vendor is later dropped from the license (a renewal with a narrower allow-list) or the
    /// connection's own BaseUrl is edited to point somewhere no longer on the list — edits aren't watched by
    /// <c>LicenseEnforcementSaveChangesInterceptor</c> (it only watches brand-new rows), so this run-time
    /// check is the only thing that ever catches that case.
    /// </summary>
    public Task EnsureSourceConnectionStillAllowedAsync(
        SourceSystemType vendorType, string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            var limits = _licenseService.Current.Limits;
            if (limits is null)
            {
                return Task.CompletedTask;
            }

            var disallowed = CheckSourceAllowList(limits, vendorType, baseUrl);
            if (disallowed is { } value)
            {
                throw new LicenseRestrictionViolationException(value.Reason, value.Message);
            }
        }
        catch (LicenseRestrictionViolationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "License quota guard failed while re-checking a source connection at run time; failing open.");
        }

        return Task.CompletedTask;
    }

    /// <summary>Pure allow-list check (source type, then hospital) shared by the create-time and run-time
    /// source connection checks — never throws itself, just reports what (if anything) is disallowed.</summary>
    private static (string Reason, string Message)? CheckSourceAllowList(
        LicenseLimits limits, SourceSystemType vendorType, string baseUrl)
    {
        if (limits.AllowedSourceTypes is { Count: > 0 } allowedSourceTypes &&
            !allowedSourceTypes.Any(allowed => string.Equals(allowed, vendorType.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            return ("SourceTypeNotAllowed", $"Your license does not permit a '{vendorType}' source connection.");
        }

        if (limits.AllowedHospitals is { Count: > 0 } allowedHospitals &&
            !allowedHospitals.Any(h =>
                string.Equals(h.Vendor, vendorType.ToString(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(h.BaseUrl, baseUrl, StringComparison.Ordinal)))
        {
            return ("HospitalNotAllowed", "Your license does not permit connecting to this hospital/endpoint.");
        }

        return null;
    }

    public async Task EnsureWorkflowQuotaAvailableAsync(CancellationToken cancellationToken)
    {
        (int Limit, int CurrentCount)? overLimit = null;

        try
        {
            var limits = _licenseService.Current.Limits;
            if (limits is null || limits.MaxWorkflows == LicenseLimits.Unlimited)
            {
                return;
            }

            var counts = await _usageCountsProvider.GetCurrentCountsAsync(cancellationToken);
            if (counts.WorkflowCount >= limits.MaxWorkflows)
            {
                overLimit = (limits.MaxWorkflows, counts.WorkflowCount);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "License quota guard failed while checking the workflow quota; failing open.");
            return;
        }

        if (overLimit is { } value)
        {
            throw new LicenseQuotaExceededException("workflows", value.Limit, value.CurrentCount);
        }
    }

    public Task EnsureResourceTypeAllowedAsync(string resourceType, CancellationToken cancellationToken)
    {
        string? disallowedMessage = null;

        try
        {
            var limits = _licenseService.Current.Limits;
            if (limits?.AllowedResourceTypes is { Count: > 0 } allowedResourceTypes &&
                !allowedResourceTypes.Any(allowed => string.Equals(allowed, resourceType, StringComparison.OrdinalIgnoreCase)))
            {
                disallowedMessage = $"Your license does not permit processing the '{resourceType}' resource type.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "License quota guard failed while checking the resource type allow-list; failing open.");
            return Task.CompletedTask;
        }

        if (disallowedMessage is not null)
        {
            throw new LicenseRestrictionViolationException("ResourceTypeNotAllowed", disallowedMessage);
        }

        return Task.CompletedTask;
    }

    public Task EnsureDestinationTypeAllowedAsync(DestinationType destinationType, CancellationToken cancellationToken)
    {
        string? disallowedMessage = null;

        try
        {
            var limits = _licenseService.Current.Limits;
            if (limits?.AllowedDestinationTypes is { Count: > 0 } allowedDestinationTypes &&
                !allowedDestinationTypes.Any(allowed => string.Equals(allowed, destinationType.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                disallowedMessage = $"Your license does not permit writing to a '{destinationType}' destination.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "License quota guard failed while checking the destination type allow-list; failing open.");
            return Task.CompletedTask;
        }

        if (disallowedMessage is not null)
        {
            throw new LicenseRestrictionViolationException("DestinationTypeNotAllowed", disallowedMessage);
        }

        return Task.CompletedTask;
    }

    public async Task EnsureCanStartNewRunAsync(CancellationToken cancellationToken)
    {
        string? blockedReason = null;
        string? blockedMessage = null;

        try
        {
            var status = _licenseService.Current;
            if (status.IsExpired)
            {
                blockedReason = "LicenseExpired";
                blockedMessage = "Your license has expired. Renew it to start new pipeline runs.";
            }
            else if (status.Limits is { } limits &&
                     (limits.MaxProcessedRecordsPerMonth != LicenseLimits.Unlimited ||
                      limits.MaxSuccessfulWorkflowExecutionsPerMonth != LicenseLimits.Unlimited))
            {
                var stats = await GetCachedExecutionStatsAsync(cancellationToken);

                if (limits.MaxProcessedRecordsPerMonth != LicenseLimits.Unlimited &&
                    stats.ProcessedRecordsThisMonth >= limits.MaxProcessedRecordsPerMonth)
                {
                    blockedReason = "ProcessedRecordsQuotaExceeded";
                    blockedMessage =
                        $"Your license allows processing a maximum of {limits.MaxProcessedRecordsPerMonth} records " +
                        $"per month; this install has already processed {stats.ProcessedRecordsThisMonth} this month.";
                }
                else if (limits.MaxSuccessfulWorkflowExecutionsPerMonth != LicenseLimits.Unlimited &&
                         stats.SuccessfulExecutionsThisMonth >= limits.MaxSuccessfulWorkflowExecutionsPerMonth)
                {
                    blockedReason = "SuccessfulExecutionsQuotaExceeded";
                    blockedMessage =
                        $"Your license allows a maximum of {limits.MaxSuccessfulWorkflowExecutionsPerMonth} " +
                        $"successful workflow executions per month; this install has already completed " +
                        $"{stats.SuccessfulExecutionsThisMonth} this month.";
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "License quota guard failed while checking whether a new run may start; failing open.");
            return;
        }

        if (blockedReason is not null)
        {
            throw new LicenseRestrictionViolationException(blockedReason, blockedMessage!);
        }
    }

    /// <summary>
    /// Short-TTL cached read of the current calendar month's execution stats (processed records, successful
    /// executions). This check sits on the pipeline-run TRIGGER path (scheduled routes, webhooks — can fire
    /// frequently), so a live query on every single trigger would be a real, avoidable cost; a monthly quota
    /// doesn't need per-request precision. Mirrors <c>ISystemSettingsCache</c>'s cached-read shape.
    /// </summary>
    private async Task<LicenseUsageExecutionStats> GetCachedExecutionStatsAsync(CancellationToken cancellationToken)
    {
        if (_memoryCache.TryGetValue(ExecutionStatsCacheKey, out LicenseUsageExecutionStats? cached) && cached is not null)
        {
            return cached;
        }

        var stats = await _executionStatsProvider.GetCurrentStatsAsync(cancellationToken);
        _memoryCache.Set(ExecutionStatsCacheKey, stats, ExecutionStatsCacheTtl);
        return stats;
    }
}
