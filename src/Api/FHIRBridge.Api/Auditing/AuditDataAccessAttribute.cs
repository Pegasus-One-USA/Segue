using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Api.Auditing;

/// <summary>
/// MVC action-filter equivalent of <see cref="DataAccessAuditFilter"/> — writes one DataAccess-category Audit
/// Trail entry per successful action result, for controller-based endpoints that return PHI or other sensitive
/// detail. <paramref name="routeIdParameterName"/>, if given, names a route value (e.g. "routeExecutionId") whose
/// value is recorded as the entity id/name.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AuditDataAccessAttribute : ActionFilterAttribute
{
    private readonly string _module;
    private readonly string _action;
    private readonly string? _routeIdParameterName;

    public AuditDataAccessAttribute(string module, string action, string? routeIdParameterName = null)
    {
        _module = module;
        _action = action;
        _routeIdParameterName = routeIdParameterName;
    }

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var executedContext = await next();

        if (executedContext.Exception is not null || executedContext.Canceled)
        {
            return;
        }

        if (executedContext.Result is IStatusCodeActionResult { StatusCode: { } statusCode }
            && (statusCode < 200 || statusCode >= 300))
        {
            return;
        }

        var httpContext = context.HttpContext;
        var currentUserService = httpContext.RequestServices.GetRequiredService<ICurrentUserService>();
        var activityAuditService = httpContext.RequestServices.GetRequiredService<IUserActivityAuditService>();

        string? entityId = _routeIdParameterName is not null
            && context.RouteData.Values.TryGetValue(_routeIdParameterName, out var routeValue)
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
    }
}
