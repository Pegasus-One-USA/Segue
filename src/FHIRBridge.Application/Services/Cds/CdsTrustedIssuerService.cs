using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services.Cds;

public sealed class CdsTrustedIssuerService : ICdsTrustedIssuerService
{
    private readonly ICdsTrustedIssuerRepository _repository;
    private readonly ICdsServiceInstanceRepository _instanceRepository;
    private readonly IUserDisplayNameResolver _userDisplayNameResolver;

    public CdsTrustedIssuerService(
        ICdsTrustedIssuerRepository repository,
        ICdsServiceInstanceRepository instanceRepository,
        IUserDisplayNameResolver userDisplayNameResolver)
    {
        _repository = repository;
        _instanceRepository = instanceRepository;
        _userDisplayNameResolver = userDisplayNameResolver;
    }

    public async Task<IReadOnlyList<CdsTrustedIssuerDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var issuers = await _repository.GetAllAsync(cancellationToken);
        return await ToDtosAsync(issuers, cancellationToken);
    }

    public async Task<CdsTrustedIssuerDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var issuer = await _repository.GetByIdAsync(id, cancellationToken);
        return issuer is null ? null : (await ToDtosAsync([issuer], cancellationToken))[0];
    }

    public async Task<CdsTrustedIssuerDto> CreateAsync(SaveCdsTrustedIssuerRequest request, CancellationToken cancellationToken)
    {
        var issuer = new CdsTrustedIssuer(
            request.Name, request.Vendor, request.Issuer, request.JwksUrl, request.JwksJson, request.Audience, request.IsEnabled,
            request.ExpectedSubject, request.FhirBaseUrl, request.AllowCallback);
        await EnsureIssuerIsFreeAsync(issuer.Issuer, excludeId: null, cancellationToken);

        await _repository.AddAsync(issuer, cancellationToken);
        return (await ToDtosAsync([issuer], cancellationToken))[0];
    }

    public async Task<CdsTrustedIssuerDto> UpdateAsync(Guid id, SaveCdsTrustedIssuerRequest request, CancellationToken cancellationToken)
    {
        var issuer = await GetRequiredAsync(id, cancellationToken);
        issuer.Update(
            request.Name, request.Vendor, request.Issuer, request.JwksUrl, request.JwksJson, request.Audience, request.IsEnabled,
            request.ExpectedSubject, request.FhirBaseUrl, request.AllowCallback);
        await EnsureIssuerIsFreeAsync(issuer.Issuer, excludeId: id, cancellationToken);

        await _repository.UpdateAsync(issuer, cancellationToken);
        return (await ToDtosAsync([issuer], cancellationToken))[0];
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var issuer = await GetRequiredAsync(id, cancellationToken);

        // An allow-list naming a deleted issuer would silently lock that service to nobody, so make the admin
        // take it off the list first.
        var instances = await _instanceRepository.GetAllAsync(cancellationToken);
        var referencing = instances.Where(instance => instance.AllowedIssuerIds.Contains(id)).Select(x => x.Title).ToArray();
        if (referencing.Length > 0)
        {
            throw new BusinessRuleException(
                $"This issuer is on the allow-list of: {string.Join(", ", referencing)}. Remove it there first.");
        }

        await _repository.DeleteAsync(issuer, cancellationToken);
    }

    private async Task EnsureIssuerIsFreeAsync(string issuer, Guid? excludeId, CancellationToken cancellationToken)
    {
        if (await _repository.IssuerExistsAsync(issuer, excludeId, cancellationToken))
        {
            throw new BusinessRuleException($"A trusted issuer with the iss '{issuer}' already exists.");
        }
    }

    private async Task<CdsTrustedIssuer> GetRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetByIdAsync(id, cancellationToken)
        ?? throw new NotFoundException("CdsTrustedIssuer", id);

    private async Task<IReadOnlyList<CdsTrustedIssuerDto>> ToDtosAsync(
        IReadOnlyList<CdsTrustedIssuer> issuers, CancellationToken cancellationToken)
    {
        var names = await _userDisplayNameResolver.ResolveAsync(
            issuers.SelectMany(x => new[] { x.CreatedBy, x.ModifiedBy }), cancellationToken);

        return issuers.Select(issuer => new CdsTrustedIssuerDto(
            issuer.Id,
            issuer.Name,
            issuer.Vendor,
            issuer.Issuer,
            issuer.JwksUrl,
            issuer.JwksJson,
            issuer.Audience,
            issuer.IsEnabled,
            issuer.CreatedOnUtc,
            Resolve(names, issuer.CreatedBy),
            issuer.ModifiedOnUtc,
            Resolve(names, issuer.ModifiedBy),
            issuer.ExpectedSubject,
            issuer.FhirBaseUrl,
            issuer.EffectiveFhirBaseUrl,
            issuer.AllowCallback)).ToArray();
    }

    private static string? Resolve(IReadOnlyDictionary<string, string> names, string? actor) =>
        actor is null ? null : names.GetValueOrDefault(actor, actor);
}
