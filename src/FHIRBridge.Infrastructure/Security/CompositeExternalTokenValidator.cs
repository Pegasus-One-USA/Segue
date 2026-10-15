using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Routes an external token to the matching per-provider validator and enforces that the provider is
/// enabled. Rejects <see cref="LoginProvider.Local"/> (that path uses password login) and any provider
/// that has no registered validator. Each validator owns its own enabled check
/// (<see cref="IProviderTokenValidator.IsEnabledAsync"/>), so adding a provider is a new class registered in
/// DI, never a modification here.
/// </summary>
public sealed class CompositeExternalTokenValidator : IExternalTokenValidator
{
    private readonly IReadOnlyDictionary<LoginProvider, IProviderTokenValidator> _validators;

    public CompositeExternalTokenValidator(IEnumerable<IProviderTokenValidator> validators)
    {
        _validators = validators.ToDictionary(v => v.Provider);
    }

    public async Task<ExternalIdentity> ValidateAsync(
        LoginProvider provider,
        string token,
        CancellationToken cancellationToken)
    {
        if (!_validators.TryGetValue(provider, out var validator))
        {
            throw new InvalidOperationException($"The '{provider}' identity provider is not enabled.");
        }

        if (!await validator.IsEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException($"The '{provider}' identity provider is not enabled.");
        }

        return await validator.ValidateAsync(token, cancellationToken);
    }
}
