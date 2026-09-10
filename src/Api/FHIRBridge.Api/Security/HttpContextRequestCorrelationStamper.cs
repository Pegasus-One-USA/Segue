using FHIRBridge.Application.Abstractions.Security;

namespace FHIRBridge.Api.Security;

/// <summary>
/// <see cref="IRequestCorrelationStamper"/> over the live <see cref="HttpContext"/> — delegates to the same
/// <see cref="WorkflowCorrelationResolver.ApplyDerived"/> the run and mint endpoints call, so every leg of one
/// workflow attempt derives its correlation id exactly one way.
/// </summary>
public sealed class HttpContextRequestCorrelationStamper : IRequestCorrelationStamper
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextRequestCorrelationStamper(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public void Stamp(Guid workflowId, string? sessionId) =>
        WorkflowCorrelationResolver.ApplyDerived(_httpContextAccessor.HttpContext, workflowId, sessionId);

    public void StampExplicit(string correlationId) =>
        WorkflowCorrelationResolver.ApplyExplicit(_httpContextAccessor.HttpContext, correlationId);
}
