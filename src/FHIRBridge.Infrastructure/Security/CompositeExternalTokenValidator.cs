using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Enums;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Routes an external token to the matching per-provider validator and enforces that the provider is
/// enabled in configuration. Rejects <see cref="LoginProvider.Local"/> (that path uses password login)
/// and any provider that has no registered validator. Entra's enabled flag is resolved live via
/// <see cref="ISystemSettingsCache"/> (SSO Configurations screen) — Google's stays appsettings-only,
/// unchanged.
/// </summary>
public sealed class CompositeExternalTokenValidator : IExternalTokenValidator
{
    private const string EntraEnabledKey = "Authentication:Entra:Enabled";

    private readonly IReadOnlyDictionary<LoginProvider, IProviderTokenValidator> _validators;
    private readonly EntraAuthenticationOptions _entra;
    private readonly GoogleAuthenticationOptions _google;
    private readonly ISystemSettingsCache _settingsCache;

    public CompositeExternalTokenValidator(
        IEnumerable<IProviderTokenValidator> validators,
        IOptions<EntraAuthenticationOptions> entra,
        IOptions<GoogleAuthenticationOptions> google,
        ISystemSettingsCache settingsCache)
    {
        _validators = validators.ToDictionary(v => v.Provider);
        _entra = entra.Value;
        _google = google.Value;
        _settingsCache = settingsCache;
    }

    public async Task<ExternalIdentity> ValidateAsync(
        LoginProvider provider,
        string token,
        CancellationToken cancellationToken)
    {
        if (!await IsProviderEnabledAsync(provider, cancellationToken))
        {
            throw new InvalidOperationException($"The '{provider}' identity provider is not enabled.");
        }

        if (!_validators.TryGetValue(provider, out var validator))
        {
            throw new InvalidOperationException("This sign-in method isn't available.");
        }

        return await validator.ValidateAsync(token, cancellationToken);
    }

    private async Task<bool> IsProviderEnabledAsync(LoginProvider provider, CancellationToken cancellationToken) => provider switch
    {
        LoginProvider.Entra => await _settingsCache.GetBoolAsync(EntraEnabledKey, _entra.Enabled, cancellationToken),
        LoginProvider.Google => _google.Enabled,
        _ => false
    };
}
