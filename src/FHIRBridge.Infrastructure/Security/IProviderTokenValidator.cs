using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// A single-provider token validator (Entra or Google). The <see cref="CompositeExternalTokenValidator"/>
/// routes an incoming <see cref="Domain.Enums.LoginProvider"/> to the matching implementation.
/// </summary>
public interface IProviderTokenValidator
{
    LoginProvider Provider { get; }

    /// <summary>Whether this provider is currently switched on (live-read settings, falling back to appsettings).</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken);

    Task<ExternalIdentity> ValidateAsync(string token, CancellationToken cancellationToken);
}
