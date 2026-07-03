using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Enums;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Routes an external token to the matching per-provider validator and enforces that the provider is
/// enabled in configuration. Rejects <see cref="LoginProvider.Local"/> (that path uses password login)
/// and any provider that has no registered validator.
/// </summary>
public sealed class CompositeExternalTokenValidator : IExternalTokenValidator
{
    private readonly IReadOnlyDictionary<LoginProvider, IProviderTokenValidator> _validators;
    private readonly EntraAuthenticationOptions _entra;
    private readonly GoogleAuthenticationOptions _google;

    public CompositeExternalTokenValidator(
        IEnumerable<IProviderTokenValidator> validators,
        IOptions<EntraAuthenticationOptions> entra,
        IOptions<GoogleAuthenticationOptions> google)
    {
        _validators = validators.ToDictionary(v => v.Provider);
        _entra = entra.Value;
        _google = google.Value;
    }

    public Task<ExternalIdentity> ValidateAsync(
        LoginProvider provider,
        string token,
        CancellationToken cancellationToken)
    {
        if (!IsProviderEnabled(provider))
        {
            throw new InvalidOperationException($"The '{provider}' identity provider is not enabled.");
        }

        if (!_validators.TryGetValue(provider, out var validator))
        {
            throw new InvalidOperationException($"No token validator is registered for provider '{provider}'.");
        }

        return validator.ValidateAsync(token, cancellationToken);
    }

    private bool IsProviderEnabled(LoginProvider provider) => provider switch
    {
        LoginProvider.Entra => _entra.Enabled,
        LoginProvider.Google => _google.Enabled,
        _ => false
    };
}
