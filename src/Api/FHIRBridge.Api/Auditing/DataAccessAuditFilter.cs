using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Api.Auditing;

/// <summary>
/// Minimal-API endpoint filter that writes one DataAccess-category Audit Trail entry per successful call —
/// attach via <see cref="DataAccessAuditEndpointExtensions.AuditDataAccess"/> instead of hand-writing the
/// RecordAsync call inline in every endpoint that returns PHI or other sensitive detail.
/// </summary>
public sealed class DataAccessAuditFilter : IEndpointFilter
{
    private readonly string _module;
    private readonly string _action;
    private readonly string? _routeIdParameterName;

    public DataAccessAuditFilter(string module, string action, string? routeIdParameterName)
    {
        _module = module;
        _action = action;
        _routeIdParameterName = routeIdParameterName;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);

        // A 404/400/etc. never actually returned PHI — only audit the successful reads.
        if (result is IStatusCodeHttpResult { StatusCode: { } statusCode } && (statusCode < 200 || statusCode >= 300))
        {
            return result;
        }

        var httpContext = context.HttpContext;
        var currentUserService = httpContext.RequestServices.GetRequiredService<ICurrentUserService>();
        var activityAuditService = httpContext.RequestServices.GetRequiredService<IUserActivityAuditService>();

        string? entityId = _routeIdParameterName is not null
            && httpContext.Request.RouteValues.TryGetValue(_routeIdParameterName, out var routeValue)
                ? routeValue?.ToString()
                : null;

        var user = currentUserService.CurrentUser;
        var userId = Guid.TryParse(user.ExternalUserId, out var parsedUserId) ? parsedUserId : (Guid?)null;
        await activityAuditService.RecordAsync(
            new RecordUserActivityRequest(
                UserId: userId,
                UserEmail: user.AuditName,
                Category: UserActivityCategories.DataAccess,
                Activity: $"{_action} for {_module} {entityId ?? "(n/a)"}",
                Status: UserActivityStatuses.Success,
                EntityName: entityId,
                EntityId: Guid.TryParse(entityId, out var parsedEntityId) ? parsedEntityId : null,
                IpAddress: user.IpAddress,
                UserAgent: user.UserAgent,
                CorrelationId: user.CorrelationId,
                Module: _module,
                Action: _action),
            httpContext.RequestAborted);

        return result;
    }
}

public static class DataAccessAuditEndpointExtensions
{
    /// <summary>
    /// Audits every successful call to this endpoint as a DataAccess event on the Audit Trail — attach to any
    /// Minimal API endpoint that returns decrypted PHI or other sensitive detail, instead of hand-writing the
    /// RecordAsync call inline. <paramref name="routeIdParameterName"/>, if given, names a route parameter (e.g.
    /// "runId") whose value is recorded as the entity id/name.
    /// </summary>
    public static RouteHandlerBuilder AuditDataAccess(
        this RouteHandlerBuilder builder, string module, string action, string? routeIdParameterName = null) =>
        builder.AddEndpointFilter(new DataAccessAuditFilter(module, action, routeIdParameterName));
}
