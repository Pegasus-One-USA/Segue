using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services.Cds;

public sealed class CdsServiceInstanceService : ICdsServiceInstanceService
{
    private readonly ICdsServiceInstanceRepository _repository;
    private readonly ICdsTrustedIssuerRepository _issuerRepository;
    private readonly ICdsServiceHandlerRegistry _handlers;
    private readonly IUserDisplayNameResolver _userDisplayNameResolver;

    public CdsServiceInstanceService(
        ICdsServiceInstanceRepository repository,
        ICdsTrustedIssuerRepository issuerRepository,
        ICdsServiceHandlerRegistry handlers,
        IUserDisplayNameResolver userDisplayNameResolver)
    {
        _repository = repository;
        _issuerRepository = issuerRepository;
        _handlers = handlers;
        _userDisplayNameResolver = userDisplayNameResolver;
    }

    public async Task<IReadOnlyList<CdsServiceInstanceDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var instances = await _repository.GetAllAsync(cancellationToken);
        return await ToDtosAsync(instances, cancellationToken);
    }

    public async Task<CdsServiceInstanceDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var instance = await _repository.GetByIdAsync(id, cancellationToken);
        return instance is null ? null : (await ToDtosAsync([instance], cancellationToken))[0];
    }

    public async Task<CdsServiceInstanceDto> CreateAsync(SaveCdsServiceInstanceRequest request, CancellationToken cancellationToken)
    {
        var (handler, settingsJson) = await ValidateAsync(request, excludeId: null, cancellationToken);
        var instance = new CdsServiceInstance(
            request.ServiceId,
            request.Title,
            handler.Key,
            request.Hook,
            DescriptionOrDefault(request, handler),
            request.IsEnabled,
            settingsJson,
            CardSourceLabelOrDefault(request),
            request.CardSourceUrl,
            request.AllowedIssuerIds,
            request.FetchMissingData,
            request.Fixes,
            request.OverrideReasons,
            request.Links);

        await _repository.AddAsync(instance, cancellationToken);
        return (await ToDtosAsync([instance], cancellationToken))[0];
    }

    public async Task<CdsServiceInstanceDto> UpdateAsync(Guid id, SaveCdsServiceInstanceRequest request, CancellationToken cancellationToken)
    {
        var instance = await GetRequiredAsync(id, cancellationToken);
        var (handler, settingsJson) = await ValidateAsync(request, excludeId: id, cancellationToken);
        instance.Update(
            request.ServiceId,
            request.Title,
            handler.Key,
            request.Hook,
            DescriptionOrDefault(request, handler),
            request.IsEnabled,
            settingsJson,
            CardSourceLabelOrDefault(request),
            request.CardSourceUrl,
            request.AllowedIssuerIds,
            request.FetchMissingData,
            request.Fixes,
            request.OverrideReasons,
            request.Links);

        await _repository.UpdateAsync(instance, cancellationToken);
        return (await ToDtosAsync([instance], cancellationToken))[0];
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var instance = await GetRequiredAsync(id, cancellationToken);
        await _repository.DeleteAsync(instance, cancellationToken);
    }

    public async Task<CdsServiceInstanceDto> SignOffAsync(Guid id, CdsSignOffRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var instance = await GetRequiredAsync(id, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.ContentVersion))
        {
            throw new BusinessRuleException("contentVersion is required: send the contentVersion of the service the clinician reviewed.");
        }

        if (!string.Equals(request.ContentVersion.Trim(), instance.ContentVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new CdsContentChangedException();
        }

        instance.SignOff(request.SignedOffBy, request.Note, DateTime.UtcNow);
        await _repository.UpdateAsync(instance, cancellationToken);
        return (await ToDtosAsync([instance], cancellationToken))[0];
    }

    public async Task<CdsServiceInstanceDto> ClearSignOffAsync(Guid id, CancellationToken cancellationToken)
    {
        var instance = await GetRequiredAsync(id, cancellationToken);
        if (instance.IsClinicallySignedOff)
        {
            instance.ClearSignOff();
            await _repository.UpdateAsync(instance, cancellationToken);
        }

        return (await ToDtosAsync([instance], cancellationToken))[0];
    }

    public async Task<CdsRequest> GetSampleRequestAsync(Guid id, CancellationToken cancellationToken)
    {
        var instance = await GetRequiredAsync(id, cancellationToken);
        var handler = _handlers.Find(instance.HandlerKey)
            ?? throw new BusinessRuleException($"The handler type '{instance.HandlerKey}' of this service is no longer available.");
        return handler.BuildSampleRequest(instance.Hook);
    }

    private async Task<(ICdsServiceHandler Handler, string SettingsJson)> ValidateAsync(
        SaveCdsServiceInstanceRequest request, Guid? excludeId, CancellationToken cancellationToken)
    {
        var handler = _handlers.Find(request.HandlerKey?.Trim())
            ?? throw new BusinessRuleException($"Unknown handler type '{request.HandlerKey}'.");

        var hook = request.Hook?.Trim();
        if (string.IsNullOrEmpty(hook) || !handler.SupportedHooks.Contains(hook, StringComparer.Ordinal))
        {
            throw new BusinessRuleException(
                $"{handler.DisplayName} answers only these hooks: {string.Join(", ", handler.SupportedHooks)}.");
        }

        var serviceId = CdsServiceInstance.NormalizeServiceId(request.ServiceId);
        if (await _repository.ServiceIdExistsAsync(serviceId, excludeId, cancellationToken))
        {
            throw new BusinessRuleException($"A CDS service with the id '{serviceId}' already exists.");
        }

        foreach (var issuerId in (request.AllowedIssuerIds ?? []).Distinct())
        {
            if (await _issuerRepository.GetByIdAsync(issuerId, cancellationToken) is null)
            {
                throw new BusinessRuleException("One of the allowed issuers no longer exists. Refresh and pick again.");
            }
        }

        var settings = CdsSettingsValues.Validate(handler.Settings, request.Settings);
        ValidateFixes(handler, request.Fixes);
        return (handler, settings.ToJsonString());
    }

    /// <summary>
    /// The handler-specific fix rules (shape rules are the domain's): every fix type must be one the handler
    /// offers; a switched-on type that needs a code must have one; each mapping uses the descriptor's code systems,
    /// triggers and required fields.
    /// </summary>
    private static void ValidateFixes(ICdsServiceHandler handler, CdsFixSettings? fixes)
    {
        if (fixes is null)
        {
            return;
        }

        var errors = new List<string>();
        foreach (var setting in fixes.FixTypes ?? [])
        {
            var key = setting?.Key?.Trim();
            var descriptor = handler.SupportedFixTypes.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));
            if (setting is null || descriptor is null)
            {
                errors.Add($"{handler.DisplayName} does not offer the fix '{key}'.");
                continue;
            }

            var mappings = setting.Mappings ?? [];
            if (setting.Enabled && descriptor.RequiresMapping && mappings.Count == 0)
            {
                errors.Add($"Fix '{descriptor.Label}': add at least one code before switching it on.");
            }

            var displayRequired = descriptor.MappingFields.Any(f => f.Key == CdsFixMappingFieldKeys.Display && f.Required);
            foreach (var mapping in mappings)
            {
                if (mapping is null)
                {
                    continue;
                }

                var system = mapping.System?.Trim();
                if (descriptor.CodeSystems is { Count: > 0 } systems && !systems.Any(s => s.Value == system))
                {
                    errors.Add($"Fix '{descriptor.Label}': the code system must be one of {string.Join(", ", systems.Select(s => s.Label))}.");
                }

                var trigger = string.IsNullOrWhiteSpace(mapping.Trigger) ? null : mapping.Trigger.Trim();
                if (trigger is not null && (descriptor.Triggers is null || !descriptor.Triggers.Any(t => t.Value == trigger)))
                {
                    errors.Add($"Fix '{descriptor.Label}': '{trigger}' is not one of its triggers.");
                }

                if (displayRequired && string.IsNullOrWhiteSpace(mapping.Display))
                {
                    errors.Add($"Fix '{descriptor.Label}': each code needs a display name.");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new BusinessRuleException(string.Join(" ", errors.Distinct(StringComparer.Ordinal)));
        }
    }

    private static string DescriptionOrDefault(SaveCdsServiceInstanceRequest request, ICdsServiceHandler handler) =>
        string.IsNullOrWhiteSpace(request.Description) ? handler.Description : request.Description;

    private static string CardSourceLabelOrDefault(SaveCdsServiceInstanceRequest request) =>
        string.IsNullOrWhiteSpace(request.CardSourceLabel) ? request.Title : request.CardSourceLabel;

    private async Task<CdsServiceInstance> GetRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetByIdAsync(id, cancellationToken)
        ?? throw new NotFoundException("CdsService", id);

    private async Task<IReadOnlyList<CdsServiceInstanceDto>> ToDtosAsync(
        IReadOnlyList<CdsServiceInstance> instances, CancellationToken cancellationToken)
    {
        var names = await _userDisplayNameResolver.ResolveAsync(
            instances.SelectMany(x => new[] { x.CreatedBy, x.ModifiedBy }), cancellationToken);

        return instances.Select(instance =>
        {
            var handler = _handlers.Find(instance.HandlerKey);
            return new CdsServiceInstanceDto(
                instance.Id,
                instance.ServiceId,
                instance.Title,
                instance.HandlerKey,
                handler?.DisplayName,
                instance.Hook,
                instance.Description,
                instance.IsEnabled,
                handler is null
                    ? CdsSettingsValues.Resolve([], instance.SettingsJson).ToJsonObject()
                    : CdsSettingsValues.Resolve(handler.Settings, instance.SettingsJson).ToJsonObject(),
                CdsSettingsValues.Resolve([], instance.SettingsJson).ToJsonObject(),
                instance.CardSourceLabel,
                instance.CardSourceUrl,
                instance.AllowedIssuerIds.ToArray(),
                handler?.GetPrefetchTemplates(instance.Hook) ?? new Dictionary<string, string>(),
                instance.CreatedOnUtc,
                Resolve(names, instance.CreatedBy),
                instance.ModifiedOnUtc,
                Resolve(names, instance.ModifiedBy),
                instance.FetchMissingData,
                instance.GetFixes(),
                instance.FixesEnabled,
                instance.GetOverrideReasons(),
                instance.GetOwnOverrideReasons() is null,
                instance.GetLinks(),
                handler?.SupportedFixTypes ?? [],
                instance.IsClinicallySignedOff,
                instance.SignedOffBy,
                instance.SignedOffAtUtc,
                instance.SignOffNote,
                handler?.UsesSampleReferenceData == true,
                instance.ContentVersion);
        }).ToArray();
    }

    private static string? Resolve(IReadOnlyDictionary<string, string> names, string? actor) =>
        actor is null ? null : names.GetValueOrDefault(actor, actor);
}

/// <summary>
/// A sign-off named a <see cref="CdsServiceInstanceDto.ContentVersion"/> that is no longer the service's: someone
/// changed what the service tells clinicians after the reviewer looked at it. The API answers 409 Conflict.
/// </summary>
public sealed class CdsContentChangedException : FHIRBridgeException
{
    public CdsContentChangedException()
        : base("The service changed since it was opened. Reload it, have the clinician review the new version, then record the sign-off again.")
    {
    }
}
