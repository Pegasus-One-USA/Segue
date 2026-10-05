using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Governance;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>Supplies the current request's correlation id when an error is queued (the same fallback the table
/// logger used to apply when it wrote the row itself).</summary>
public sealed class CurrentUserCorrelationIdAccessor : ICorrelationIdAccessor
{
    private readonly ICurrentUserService _currentUser;

    public CurrentUserCorrelationIdAccessor(ICurrentUserService currentUser)
    {
        _currentUser = currentUser;
    }

    public string? CorrelationId => _currentUser.CurrentUser.CorrelationId;
}
