using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Exceptions;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Application.Validation;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Enums;
using FHIRBridge.SharedKernel.Exceptions;
using FluentValidation;
using FHIRBridge.Observability.Logging;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Application.Services;

/// <summary>
/// De-tenanted configuration CRUD. Successor to <c>UnifiedTenantConfigurationService</c>: talks directly to the flat
/// <see cref="IConfigurationRepository"/> instead of loading/saving a Tenant aggregate. Resource "groups" are derived
/// from routes by resolving each route's resource type through its mapping profile.
/// </summary>
public sealed class ConfigurationService : IConfigurationService
{
    private readonly IConfigurationRepository _repository;
    private readonly ISourceCapabilityRepository _capabilityRepository;
    private readonly ISourceCapabilityDiscoveryService _capabilityDiscoveryService;
    private readonly ISecretWriter _secretWriter;
    private readonly ISecretProvider _secretProvider;
    private readonly ISqlConnectionSecretMerger _sqlConnectionSecretMerger;
    private readonly ITenantSecretVaultResolver _tenantSecretVaultResolver;
    private readonly IParentReferenceResolver _parentReferenceResolver;
    private readonly IValidator<CreateMappingProfileRequest> _mappingProfileValidator;
    private readonly IValidator<CreateDestinationConfigurationRequest> _destinationConfigurationValidator;
    private readonly IUserDisplayNameResolver _userDisplayNameResolver;
    private readonly ILogger<ConfigurationService> _logger;

    public ConfigurationService(
        IConfigurationRepository repository,
        ISourceCapabilityRepository capabilityRepository,
        ISourceCapabilityDiscoveryService capabilityDiscoveryService,
        ISecretWriter secretWriter,
        ISecretProvider secretProvider,
        ISqlConnectionSecretMerger sqlConnectionSecretMerger,
        ITenantSecretVaultResolver tenantSecretVaultResolver,
        IParentReferenceResolver parentReferenceResolver,
        IValidator<CreateMappingProfileRequest> mappingProfileValidator,
        IValidator<CreateDestinationConfigurationRequest> destinationConfigurationValidator,
        IUserDisplayNameResolver userDisplayNameResolver,
        ILogger<ConfigurationService> logger)
    {
        _repository = repository;
        _capabilityRepository = capabilityRepository;
        _capabilityDiscoveryService = capabilityDiscoveryService;
        _secretWriter = secretWriter;
        _secretProvider = secretProvider;
        _sqlConnectionSecretMerger = sqlConnectionSecretMerger;
        _tenantSecretVaultResolver = tenantSecretVaultResolver;
        _parentReferenceResolver = parentReferenceResolver;
        _mappingProfileValidator = mappingProfileValidator;
        _destinationConfigurationValidator = destinationConfigurationValidator;
        _userDisplayNameResolver = userDisplayNameResolver;
        _logger = logger;
    }

    private async Task ValidateRequestAsync<T>(IValidator<T> validator, T request, CancellationToken cancellationToken)
    {
        var result = await validator.ValidateAsync(request, cancellationToken);
        if (!result.IsValid)
        {
            var ruleConflicts = result.Errors
                .Select(f => f.CustomState)
                .OfType<TransformationRuleTypeConflict>()
                .ToList();
            throw new RequestValidationException(
                new Dictionary<string, string[]>(result.ToDictionary()),
                ruleConflicts.Count > 0 ? ruleConflicts : null);
        }
    }

    public async Task<SourceConnectionDto> AddSourceConnectionAsync(
        CreateSourceConnectionRequest request,
        CancellationToken cancellationToken)
    {
        await ValidateSourceConnectionRequestAsync(request, excludeId: null, cancellationToken);
        // License source-connection quota/allow-list enforcement lives centrally in
        // LicenseEnforcementSaveChangesInterceptor (watches for a newly-Added SourceConnection row).
        var authentication = await WriteInlineClientSecretAsync(request.Authentication, cancellationToken);
        var sourceConnection = new SourceConnection(
            request.Name,
            request.SourceSystemType,
            request.BaseUrl,
            ConfigurationMapper.ToDomain(authentication),
            request.ApplicationType,
            ConfigurationMapper.ToDomain(request.Interactive),
            ConfigurationMapper.ToDomain(request.Retrieval));

        await _repository.AddSourceConnectionAsync(sourceConnection, cancellationToken);

        // First-time-setup events are logged at Information so a support question about a brand-new tenant can be
        // answered from the log alone — what was configured, against which EHR, and how it retrieves. Secrets are
        // never included: only the reference/endpoint metadata, never a client secret or private key.
        _logger.LogInformation(
            LogEvents.SourceConnectionCreated,
            "Source connection '{SourceName}' ({SourceConnectionId}) created. SourceSystemType={SourceSystemType} " +
            "ApplicationType={ApplicationType} BaseUrl={BaseUrl} RetrievalMethod={RetrievalMethod} " +
            "IncrementalSync={IncrementalSync} ResourceTypes=[{ResourceTypes}]",
            sourceConnection.Name, sourceConnection.Id, sourceConnection.SourceSystemType,
            sourceConnection.ApplicationType, sourceConnection.BaseUrl,
            sourceConnection.Retrieval?.RetrievalMethod ?? "none",
            sourceConnection.Retrieval?.IncrementalSyncEnabled,
            string.Join(", ", sourceConnection.Retrieval?.ResourceTypes ?? []));

        return ConfigurationMapper.ToDto(sourceConnection);
    }

    public async Task<SourceConnectionDto?> GetSourceConnectionByIdAsync(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await _repository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
        return sourceConnection is null ? null : ConfigurationMapper.ToDto(sourceConnection);
    }

    public async Task<SourceConnectionDto> UpdateSourceConnectionAsync(
        Guid sourceConnectionId,
        CreateSourceConnectionRequest request,
        CancellationToken cancellationToken)
    {
        await ValidateSourceConnectionRequestAsync(request, sourceConnectionId, cancellationToken);
        var resolvedRequestAuthentication = await WriteInlineClientSecretAsync(request.Authentication, cancellationToken);
        var sourceConnection = await GetSourceConnectionRequiredAsync(sourceConnectionId, cancellationToken);
        var authentication = PreserveSecretsIfBlank(ConfigurationMapper.ToDomain(resolvedRequestAuthentication), sourceConnection.Authentication);
        sourceConnection.Update(
            request.Name,
            request.SourceSystemType,
            request.BaseUrl,
            authentication,
            request.ApplicationType,
            ConfigurationMapper.ToDomain(request.Interactive),
            ConfigurationMapper.ToDomain(request.Retrieval));

        // Mapped immediately after Update(), before SaveChangesAsync — Update() reassigns brand-new owned-value-
        // object instances (Authentication/Interactive/Retrieval) onto this tracked entity, and EF Core's post-save
        // fixup for a *replaced* owned reference leaves that navigation null on this in-memory instance afterward
        // (the new values still land correctly in the database; only this object's own property goes stale/null).
        // Mapping now, while the in-memory state is still guaranteed intact, sidesteps that entirely.
        var updatedDto = ConfigurationMapper.ToDto(sourceConnection);

        await _repository.UpdateSourceConnectionAsync(sourceConnection, cancellationToken);

        // Logs the resulting state, not a before/after diff: Update() has already replaced the owned value objects
        // by this point, so the prior values are gone. Comparing two of these events over time is what identifies
        // the change — which is usually the question when a connection that worked yesterday stops.
        _logger.LogInformation(
            LogEvents.SourceConnectionUpdated,
            "Source connection '{SourceName}' ({SourceConnectionId}) updated. SourceSystemType={SourceSystemType} " +
            "ApplicationType={ApplicationType} BaseUrl={BaseUrl} TokenEndpoint={TokenEndpoint} ClientId={ClientId} " +
            "RetrievalMethod={RetrievalMethod} IncrementalSync={IncrementalSync} ResourceTypes=[{ResourceTypes}]",
            sourceConnection.Name, sourceConnection.Id, sourceConnection.SourceSystemType,
            sourceConnection.ApplicationType, sourceConnection.BaseUrl,
            sourceConnection.Authentication.TokenEndpoint, sourceConnection.Authentication.ClientId,
            sourceConnection.Retrieval?.RetrievalMethod ?? "none",
            sourceConnection.Retrieval?.IncrementalSyncEnabled,
            string.Join(", ", sourceConnection.Retrieval?.ResourceTypes ?? []));

        return updatedDto;
    }

    public async Task<SourceConnectionDto> SetSourceConnectionEnabledAsync(
        Guid sourceConnectionId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await GetSourceConnectionRequiredAsync(sourceConnectionId, cancellationToken);
        sourceConnection.SetEnabled(isEnabled);

        await _repository.UpdateSourceConnectionAsync(sourceConnection, cancellationToken);

        // A disabled source makes every route depending on it silently skip (see RouteDependenciesAreEnabled),
        // with no error anywhere — so the toggle itself is the only record of why runs stopped.
        _logger.LogInformation(
            LogEvents.SourceConnectionUpdated,
            "Source connection '{SourceName}' ({SourceConnectionId}) was {EnabledState}.",
            sourceConnection.Name, sourceConnection.Id, isEnabled ? "enabled" : "disabled");

        return ConfigurationMapper.ToDto(sourceConnection);
    }

    /// <summary>
    /// Provisions a wizard-typed client secret into the secret store, mirroring how
    /// <see cref="AddDestinationConfigurationAsync"/>/<see cref="UpdateDestinationConfigurationAsync"/> handle
    /// <c>InlineSecret</c>. No-op when the request carries no raw secret (an unedited "Existing Source" reuse, or
    /// a non-secret auth method) — the KeyVaultName/SecretName reference then just points at whatever was already
    /// provisioned, or nothing has ever authenticated with a secret for that connection. Returns the
    /// <paramref name="authentication"/> to actually map to the domain, with <c>ClientSecretKeyVaultName</c>
    /// replaced by the resolved vault name when a secret was written — callers must use the returned value, not
    /// the original parameter, when building the domain entity.
    /// </summary>
    private async Task<SourceAuthenticationDto> WriteInlineClientSecretAsync(
        SourceAuthenticationDto authentication, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authentication.InlineClientSecret))
        {
            return authentication;
        }

        if (string.IsNullOrWhiteSpace(authentication.ClientSecretKeyVaultName) ||
            string.IsNullOrWhiteSpace(authentication.ClientSecretName))
        {
            throw new InvalidOperationException("A client secret Key Vault name and secret name are required to store the client secret.");
        }

        var resolvedVaultName = _tenantSecretVaultResolver.ResolveVaultName(authentication.ClientSecretKeyVaultName);
        var secretReference = new SecretReference(resolvedVaultName, authentication.ClientSecretName);
        await _secretWriter.WriteSecretAsync(secretReference, authentication.InlineClientSecret, cancellationToken);

        return authentication with { ClientSecretKeyVaultName = resolvedVaultName };
    }

    // Neither the canvas rebuild path nor the entity-mode edit form ever re-displays a previously stored secret,
    // so a re-save with blank Client Secret / Private Key fields is ambiguous between "nothing changed" and
    // "clear it" — and every caller today means the former. Only an explicit new InlineClientSecret or key-vault
    // reference in the request should actually replace what's stored; a blank field on update preserves it.
    private static SourceAuthenticationConfiguration PreserveSecretsIfBlank(
        SourceAuthenticationConfiguration requested, SourceAuthenticationConfiguration existing)
    {
        var clientSecret = requested.ClientSecret ?? existing.ClientSecret;
        var privateKey = requested.PrivateKey ?? existing.PrivateKey;
        if (ReferenceEquals(clientSecret, requested.ClientSecret) && ReferenceEquals(privateKey, requested.PrivateKey))
        {
            return requested;
        }

        return new SourceAuthenticationConfiguration(
            requested.AuthenticationType,
            requested.ClientId,
            requested.TokenEndpoint,
            requested.Scopes,
            clientSecret,
            privateKey,
            requested.KeyId,
            requested.JwksUrl,
            requested.DiscoveredScopes,
            requested.PracticeId,
            requested.AuthPlacement,
            requested.AuthorizationEndpoint);
    }

    public async Task DeleteSourceConnectionAsync(Guid sourceConnectionId, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            LogEvents.SourceConnectionDeleted,
            "Source connection {SourceConnectionId} is being deleted.", sourceConnectionId);

        var sourceConnection = await GetSourceConnectionRequiredAsync(sourceConnectionId, cancellationToken);

        await _repository.DeleteSourceConnectionAsync(sourceConnection, cancellationToken);
    }

    public async Task<IReadOnlyList<SourceConfigurationDto>> GetSourceConfigurationsAsync(CancellationToken cancellationToken)
    {
        var configurations = await _repository.GetSourceConfigurationsAsync(cancellationToken);
        return configurations.Select(ConfigurationMapper.ToDto).ToList();
    }

    public async Task<SourceConfigurationDto> AddSourceConfigurationAsync(
        CreateSourceConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureConnectionExistsAsync(request.ConnectionId, cancellationToken);

        var sourceConfiguration = new SourceConfiguration(
            request.ConnectionId,
            request.Name,
            request.Scopes,
            ConfigurationMapper.ToDomain(request.Retrieval));

        await _repository.AddSourceConfigurationAsync(sourceConfiguration, cancellationToken);

        return ConfigurationMapper.ToDto(sourceConfiguration);
    }

    public async Task<SourceConfigurationDto?> GetSourceConfigurationByIdAsync(
        Guid sourceConfigurationId,
        CancellationToken cancellationToken)
    {
        var sourceConfiguration = await _repository.GetSourceConfigurationAsync(sourceConfigurationId, cancellationToken);
        return sourceConfiguration is null ? null : ConfigurationMapper.ToDto(sourceConfiguration);
    }

    public async Task<SourceConfigurationDto> UpdateSourceConfigurationAsync(
        Guid sourceConfigurationId,
        CreateSourceConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureConnectionExistsAsync(request.ConnectionId, cancellationToken);
        var sourceConfiguration = await GetSourceConfigurationRequiredAsync(sourceConfigurationId, cancellationToken);

        if (sourceConfiguration.ConnectionId != request.ConnectionId)
        {
            throw new InvalidOperationException(
                "A source configuration's connection cannot be changed after creation. Create a new configuration instead.");
        }

        sourceConfiguration.Update(
            request.Name,
            request.Scopes,
            ConfigurationMapper.ToDomain(request.Retrieval));

        // Mapped immediately after Update(), before SaveChangesAsync — see the identical comment in
        // UpdateSourceConnectionAsync: Update() reassigns a brand-new owned Retrieval instance, and EF Core's
        // post-save fixup for a replaced owned reference can leave that navigation null on this in-memory instance
        // afterward (the database write itself is unaffected).
        var updatedDto = ConfigurationMapper.ToDto(sourceConfiguration);

        await _repository.UpdateSourceConfigurationAsync(sourceConfiguration, cancellationToken);

        return updatedDto;
    }

    public async Task DeleteSourceConfigurationAsync(Guid sourceConfigurationId, CancellationToken cancellationToken)
    {
        var sourceConfiguration = await GetSourceConfigurationRequiredAsync(sourceConfigurationId, cancellationToken);
        await _repository.DeleteSourceConfigurationAsync(sourceConfiguration, cancellationToken);
    }

    private async Task EnsureConnectionExistsAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await _repository.GetSourceConnectionAsync(connectionId, cancellationToken);
        if (connection is null)
        {
            throw new NotFoundException("SourceConnection", connectionId);
        }
    }

    private async Task<SourceConfiguration> GetSourceConfigurationRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetSourceConfigurationAsync(id, cancellationToken)
        ?? throw new NotFoundException("SourceConfiguration", id);

    /// <summary>
    /// Resolves which <see cref="SourceConfiguration"/> a newly-created mapping profile uses. When the caller
    /// explicitly picked an existing configuration (a portal that knows about reusable connections), it's reused
    /// as-is — no new row, no duplicated scopes/retrieval. When the caller doesn't supply one (today's portal,
    /// unaware of this concept), a new configuration is auto-provisioned from the connection's current
    /// Authentication.Scopes/Retrieval, mirroring the Slice 1 migration backfill so behavior is unchanged for
    /// callers that haven't adopted the new concept yet.
    /// </summary>
    private async Task<Guid> ResolveSourceConfigurationForCreateAsync(
        Guid sourceConnectionId,
        Guid? requestedSourceConfigurationId,
        CancellationToken cancellationToken)
    {
        if (requestedSourceConfigurationId is { } requestedId)
        {
            var requested = await GetSourceConfigurationRequiredAsync(requestedId, cancellationToken);
            if (requested.ConnectionId != sourceConnectionId)
            {
                throw new InvalidOperationException(
                    "The selected source configuration does not belong to the selected source connection.");
            }

            return requested.Id;
        }

        return await AutoProvisionSourceConfigurationAsync(sourceConnectionId, cancellationToken);
    }

    /// <summary>
    /// Same resolution as <see cref="ResolveSourceConfigurationForCreateAsync"/>, except that when the caller
    /// doesn't supply a configuration and the connection hasn't changed, the mapping's existing configuration is
    /// kept unchanged rather than auto-provisioning a fresh one on every save (which would otherwise strand a new,
    /// unused row per edit).
    /// </summary>
    private async Task<Guid> ResolveSourceConfigurationForUpdateAsync(
        MappingProfile mappingProfile,
        Guid sourceConnectionId,
        Guid? requestedSourceConfigurationId,
        CancellationToken cancellationToken)
    {
        if (requestedSourceConfigurationId is not null)
        {
            return await ResolveSourceConfigurationForCreateAsync(sourceConnectionId, requestedSourceConfigurationId, cancellationToken);
        }

        if (mappingProfile.SourceConfigurationId is { } existingId && mappingProfile.SourceConnectionId == sourceConnectionId)
        {
            return existingId;
        }

        // The connection changed (or this profile predates the split and somehow has no configuration yet) —
        // provision a fresh configuration under the new connection rather than reusing one tied to a different
        // connection.
        return await AutoProvisionSourceConfigurationAsync(sourceConnectionId, cancellationToken);
    }

    private async Task<Guid> AutoProvisionSourceConfigurationAsync(Guid sourceConnectionId, CancellationToken cancellationToken)
    {
        var connection = await GetSourceConnectionRequiredAsync(sourceConnectionId, cancellationToken);
        // Clone, don't share, the owned Retrieval instance: connection.Retrieval is already tracked as owned
        // by the SourceConnection entity, so handing that exact object to a new SourceConfiguration makes EF
        // try to track the same CLR instance under two owners at once — see SourceRetrievalConfiguration.Clone.
        var configuration = new SourceConfiguration(
            connection.Id,
            connection.Name,
            connection.Authentication.Scopes,
            connection.Retrieval?.Clone());

        await _repository.AddSourceConfigurationAsync(configuration, cancellationToken);
        return configuration.Id;
    }

    public async Task<WebhookConfigurationDto> AddWebhookConfigurationAsync(
        CreateWebhookConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var webhookConfiguration = new WebhookConfiguration(
            request.SourceConnectionId,
            request.ResourceType,
            request.Name,
            request.Path,
            request.IsEnabled);

        await _repository.AddWebhookAsync(webhookConfiguration, cancellationToken);

        return ConfigurationMapper.ToDto(webhookConfiguration);
    }

    public async Task<WebhookConfigurationDto> SetWebhookConfigurationEnabledAsync(
        Guid webhookConfigurationId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var webhookConfiguration = await GetWebhookRequiredAsync(webhookConfigurationId, cancellationToken);
        webhookConfiguration.SetEnabled(isEnabled);

        await _repository.UpdateWebhookAsync(webhookConfiguration, cancellationToken);

        return ConfigurationMapper.ToDto(webhookConfiguration);
    }

    public async Task<DestinationConfigurationDto> AddDestinationConfigurationAsync(
        CreateDestinationConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        await ValidateRequestAsync(_destinationConfigurationValidator, request, cancellationToken);
        // License destination-type allow-list enforcement lives centrally in
        // LicenseEnforcementSaveChangesInterceptor (watches for a newly-Added DestinationConfiguration row).

        var inlineSecret = await ResolveInlineSecretAsync(request, cancellationToken);

        var secretReference = new SecretReference(request.KeyVaultName, request.SecretName);
        if (!string.IsNullOrWhiteSpace(inlineSecret))
        {
            secretReference = new SecretReference(
                _tenantSecretVaultResolver.ResolveVaultName(request.KeyVaultName), request.SecretName);
            await _secretWriter.WriteSecretAsync(secretReference, inlineSecret!, cancellationToken);
        }

        var destinationConfiguration = new DestinationConfiguration(
            request.Name,
            request.DestinationType,
            secretReference,
            request.Target,
            request.ConnectionMetadataJson);
        destinationConfiguration.SetDeIdentificationProfile(request.DeIdentificationProfileId);

        await _repository.AddDestinationAsync(destinationConfiguration, cancellationToken);

        // Target is the server/database/container the writer will address; the secret behind SecretReference is
        // deliberately never logged, only the vault/name pointing at it.
        _logger.LogInformation(
            LogEvents.DestinationCreated,
            "Destination '{DestinationName}' ({DestinationId}) created. DestinationType={DestinationType} " +
            "Target={DestinationTarget} SecretVault={SecretVaultName} SecretName={SecretName} " +
            "DeIdentificationProfileId={DeIdentificationProfileId}",
            destinationConfiguration.Name, destinationConfiguration.Id, destinationConfiguration.DestinationType,
            destinationConfiguration.Target, secretReference.KeyVaultName, secretReference.SecretName,
            request.DeIdentificationProfileId);

        return ConfigurationMapper.ToDto(destinationConfiguration);
    }

    /// <summary>
    /// When this create request is forking a new connection off an existing one the user edited
    /// (<see cref="CreateDestinationConfigurationRequest.InheritSecretFromDestinationId"/> set) and the freshly
    /// built <see cref="CreateDestinationConfigurationRequest.InlineSecret"/> is missing credentials (the
    /// wizard never re-populates a secret field from the API), resolves the connection being forked from and
    /// splices its stored credentials in via <see cref="ISqlConnectionSecretMerger"/> — so a fork triggered by
    /// an unrelated field (e.g. "Require SSL") doesn't force the user to retype a password they never meant to
    /// change. Falls back to <c>request.InlineSecret</c> unchanged (today's behavior) whenever there's nothing
    /// to inherit from, the old destination/secret can't be resolved, or the merger finds nothing to do.
    /// </summary>
    private async Task<string?> ResolveInlineSecretAsync(
        CreateDestinationConfigurationRequest request, CancellationToken cancellationToken)
    {
        if (request.InheritSecretFromDestinationId is not { } inheritFromId
            || string.IsNullOrWhiteSpace(request.InlineSecret))
        {
            return request.InlineSecret;
        }

        var source = await _repository.GetDestinationAsync(inheritFromId, cancellationToken);
        if (source is null) return request.InlineSecret;

        string existingSecret;
        try
        {
            existingSecret = await _secretProvider.GetSecretAsync(source.SecretReference, cancellationToken);
        }
        catch (SecretNotConfiguredException)
        {
            return request.InlineSecret;
        }

        var merged = _sqlConnectionSecretMerger.TryInheritCredentials(
            request.DestinationType, existingSecret, request.InlineSecret!);
        return merged ?? request.InlineSecret;
    }

    public async Task<PagedResult<DestinationConfigurationDto>> GetDestinationConfigurationsPagedAsync(
        DestinationFilter filter,
        int page,
        int pageSize,
        string? sortBy,
        string? sortOrder,
        CancellationToken cancellationToken)
    {
        var result = await _repository.GetDestinationsPagedAsync(filter, page, pageSize, sortBy, sortOrder, cancellationToken);
        var dtos = result.Items.Select(ConfigurationMapper.ToDto).ToList();

        var names = await _userDisplayNameResolver.ResolveAsync(
            dtos.SelectMany(dto => new[] { dto.CreatedBy, dto.ModifiedBy }), cancellationToken);
        var resolved = dtos.Select(dto => dto with
        {
            CreatedBy = dto.CreatedBy is { } createdBy ? names.GetValueOrDefault(createdBy, createdBy) : null,
            ModifiedBy = dto.ModifiedBy is { } modifiedBy ? names.GetValueOrDefault(modifiedBy, modifiedBy) : null,
        }).ToList();

        return new PagedResult<DestinationConfigurationDto>(
            resolved,
            result.TotalCount,
            result.Page,
            result.PageSize);
    }

    public async Task<DestinationConfigurationDto> UpdateDestinationConfigurationAsync(
        Guid destinationId,
        CreateDestinationConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        await ValidateRequestAsync(_destinationConfigurationValidator, request, cancellationToken);

        var destinationConfiguration = await GetDestinationRequiredAsync(destinationId, cancellationToken);

        // Same gate as the delete path below: once a destination has actually received pipeline data, its
        // identity is referenced by existing execution history and audit records, so it becomes view-only.
        // Editing it in place — repointing it at a different server, table, or secret — would silently
        // retarget what that history describes.
        await EnsureDestinationHasNoExecutionHistoryAsync(destinationConfiguration, "edited", cancellationToken);

        // Neither the destination list dialog nor the wizard canvas flow ever re-displays a previously stored
        // secret, so KeyVaultName/SecretName on a re-save are not a reliable signal — CreateDestinationConfiguration
        // RequestValidator requires them non-empty on every request (including edits that don't touch the secret
        // at all), so a caller can end up resending stale or even freshly-fabricated values alongside a blank
        // InlineSecret. InlineSecret itself is the only unambiguous "the user actually wants to replace the
        // secret" signal; when it's blank, keep the entity's current reference untouched — mirrors
        // PreserveSecretsIfBlank's rationale for SourceConnection above.
        var secretReference = destinationConfiguration.SecretReference;
        if (!string.IsNullOrWhiteSpace(request.InlineSecret))
        {
            secretReference = new SecretReference(
                _tenantSecretVaultResolver.ResolveVaultName(request.KeyVaultName), request.SecretName);
            await _secretWriter.WriteSecretAsync(secretReference, request.InlineSecret!, cancellationToken);
        }

        destinationConfiguration.Update(
            request.Name,
            request.DestinationType,
            secretReference,
            request.Target,
            request.ConnectionMetadataJson ?? destinationConfiguration.ConnectionMetadataJson);
        destinationConfiguration.SetDeIdentificationProfile(request.DeIdentificationProfileId);

        // Mapped immediately after Update(), before SaveChangesAsync — see the identical comment in
        // UpdateSourceConnectionAsync: Update() reassigns a brand-new owned SecretReference instance, and EF
        // Core's post-save fixup for a replaced owned reference can leave that navigation null on this in-memory
        // instance afterward (the database write itself is unaffected).
        var updatedDto = ConfigurationMapper.ToDto(destinationConfiguration);

        await _repository.UpdateDestinationAsync(destinationConfiguration, cancellationToken);

        return updatedDto;
    }

    public async Task<DestinationConfigurationDto> SetDestinationConfigurationEnabledAsync(
        Guid destinationId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var destinationConfiguration = await GetDestinationRequiredAsync(destinationId, cancellationToken);
        destinationConfiguration.SetEnabled(isEnabled);

        await _repository.UpdateDestinationAsync(destinationConfiguration, cancellationToken);

        // A disabled destination silently stops receiving data while its workflow keeps reporting success, so the
        // toggle itself needs to be on record.
        _logger.LogInformation(
            LogEvents.DestinationUpdated,
            "Destination '{DestinationName}' ({DestinationId}, {DestinationType}) was {EnabledState}.",
            destinationConfiguration.Name, destinationConfiguration.Id, destinationConfiguration.DestinationType,
            isEnabled ? "enabled" : "disabled");

        return ConfigurationMapper.ToDto(destinationConfiguration);
    }

    public async Task<DestinationConfigurationDto> SetDestinationConfigurationDeIdentificationProfileAsync(
        Guid destinationId,
        Guid? deIdentificationProfileId,
        CancellationToken cancellationToken)
    {
        var destinationConfiguration = await GetDestinationRequiredAsync(destinationId, cancellationToken);
        destinationConfiguration.SetDeIdentificationProfile(deIdentificationProfileId);

        await _repository.UpdateDestinationAsync(destinationConfiguration, cancellationToken);

        // Same rationale as the enable/disable toggle above: changing which redactions apply to a destination
        // silently changes what PHI leaves the platform, so the change itself needs to be on record.
        _logger.LogInformation(
            LogEvents.DestinationUpdated,
            "Destination '{DestinationName}' ({DestinationId}, {DestinationType}) de-identification profile was {ProfileState}.",
            destinationConfiguration.Name,
            destinationConfiguration.Id,
            destinationConfiguration.DestinationType,
            deIdentificationProfileId is { } profileId ? $"set to {profileId}" : "cleared");

        return ConfigurationMapper.ToDto(destinationConfiguration);
    }

    public async Task DeleteDestinationConfigurationAsync(Guid destinationId, CancellationToken cancellationToken)
    {
        var destinationConfiguration = await GetDestinationRequiredAsync(destinationId, cancellationToken);
        await EnsureDestinationHasNoExecutionHistoryAsync(destinationConfiguration, "deleted", cancellationToken);

        await _repository.RemoveDestinationAsync(destinationConfiguration, cancellationToken);

        _logger.LogInformation(
            LogEvents.DestinationDeleted,
            "Destination '{DestinationName}' ({DestinationId}, {DestinationType}) was deleted.",
            destinationConfiguration.Name, destinationConfiguration.Id, destinationConfiguration.DestinationType);
    }

    public Task<bool> HasDestinationExecutionHistoryAsync(Guid destinationId, CancellationToken cancellationToken) =>
        _repository.HasDestinationExecutionHistoryAsync(destinationId, cancellationToken);

    private async Task EnsureDestinationHasNoExecutionHistoryAsync(
        DestinationConfiguration destinationConfiguration,
        string attemptedAction,
        CancellationToken cancellationToken)
    {
        if (await _repository.HasDestinationExecutionHistoryAsync(destinationConfiguration.Id, cancellationToken))
        {
            throw new BusinessRuleException(
                $"Destination configuration '{destinationConfiguration.Name}' cannot be {attemptedAction} because it has pipeline execution history. View only.");
        }
    }

    public async Task<MappingProfileDto> AddMappingProfileAsync(
        CreateMappingProfileRequest request,
        CancellationToken cancellationToken)
    {
        await ValidateRequestAsync(_mappingProfileValidator, request, cancellationToken);
        await EnsureSourceSupportsResourceTypeAsync(request.SourceConnectionId, request.ResourceType, cancellationToken);
        var sourceConfigurationId = await ResolveSourceConfigurationForCreateAsync(
            request.SourceConnectionId, request.SourceConfigurationId, cancellationToken);
        var mappingProfile = new MappingProfile(
            request.Name,
            request.ResourceType,
            request.SourceConnectionId,
            request.DestinationId,
            request.DestinationObject,
            request.Fields.Select(ConfigurationMapper.ToDomain),
            sourceConfigurationId);

        await _repository.AddMappingProfileAsync(mappingProfile, cancellationToken);

        _logger.LogInformation(
            LogEvents.MappingProfileSaved,
            "Mapping profile '{MappingProfileName}' ({MappingProfileId}) created for {ResourceType}: " +
            "SourceConnectionId={SourceConnectionId} DestinationId={DestinationId} " +
            "DestinationObject={DestinationObject} FieldCount={FieldCount}",
            mappingProfile.Name, mappingProfile.Id, mappingProfile.ResourceType,
            mappingProfile.SourceConnectionId, mappingProfile.DestinationId,
            mappingProfile.DestinationObject, mappingProfile.Fields.Count);

        return ConfigurationMapper.ToDto(mappingProfile);
    }

    public async Task<MappingProfileDto> UpdateMappingProfileAsync(
        Guid mappingProfileId,
        CreateMappingProfileRequest request,
        CancellationToken cancellationToken)
    {
        await ValidateRequestAsync(_mappingProfileValidator, request, cancellationToken);
        await EnsureSourceSupportsResourceTypeAsync(request.SourceConnectionId, request.ResourceType, cancellationToken);
        var mappingProfile = await GetMappingProfileRequiredAsync(mappingProfileId, cancellationToken);
        var sourceConfigurationId = await ResolveSourceConfigurationForUpdateAsync(
            mappingProfile, request.SourceConnectionId, request.SourceConfigurationId, cancellationToken);
        mappingProfile.Update(
            request.Name,
            request.ResourceType,
            request.SourceConnectionId,
            request.DestinationId,
            request.DestinationObject,
            request.Fields.Select(ConfigurationMapper.ToDomain),
            sourceConfigurationId);

        // Mapped immediately after Update(), before SaveChangesAsync — see the identical comment in
        // UpdateSourceConnectionAsync: Update() replaces the entire owned Fields collection, and EF Core's
        // post-save fixup for replaced owned collections can leave stale/null state on this in-memory instance
        // afterward (the database write itself is unaffected).
        var updatedDto = ConfigurationMapper.ToDto(mappingProfile);

        await _repository.UpdateMappingProfileAsync(mappingProfile, cancellationToken);

        return updatedDto;
    }

    public async Task<MappingProfileDto?> FindMappingProfileAsync(
        string resourceType, Guid sourceConnectionId, Guid destinationId, CancellationToken cancellationToken)
    {
        var mappingProfile = await _repository.FindMappingProfileAsync(
            resourceType, sourceConnectionId, destinationId, cancellationToken);
        return mappingProfile is null ? null : ConfigurationMapper.ToDto(mappingProfile);
    }

    public async Task<MappingProfileDto> SetMappingProfileEnabledAsync(
        Guid mappingProfileId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var mappingProfile = await GetMappingProfileRequiredAsync(mappingProfileId, cancellationToken);
        mappingProfile.SetEnabled(isEnabled);

        await _repository.UpdateMappingProfileAsync(mappingProfile, cancellationToken);

        return ConfigurationMapper.ToDto(mappingProfile);
    }

    public async Task<MappingProfileDto> PromoteMappingProfileToMasterAsync(
        Guid sourceMappingProfileId, string masterName, CancellationToken cancellationToken)
    {
        var source = await GetMappingProfileRequiredAsync(sourceMappingProfileId, cancellationToken);

        // Always a brand-new row, never a find-and-overwrite of an existing profile — the same discipline that
        // fixed the reverse direction (a workflow save no longer searches for and adopts another workflow's
        // profile). Promoting silently reusing/overwriting an existing master by some derived key would
        // reintroduce that exact bug one hop further along: a second workflow promoting its own mapping could
        // clobber a master the first workflow's clones already depend on.
        var masterProfile = new MappingProfile(
            masterName,
            source.ResourceType,
            source.SourceConnectionId,
            source.DestinationId,
            source.DestinationObject,
            source.Fields);

        await _repository.AddMappingProfileAsync(masterProfile, cancellationToken);

        return ConfigurationMapper.ToDto(masterProfile);
    }

    public async Task<PagedResult<MappingProfileDto>> GetMappingProfilesPagedAsync(
        MappingProfileFilter filter,
        int page,
        int pageSize,
        string? sortBy,
        string? sortOrder,
        CancellationToken cancellationToken)
    {
        var result = await _repository.GetMappingProfilesPagedAsync(filter, page, pageSize, sortBy, sortOrder, cancellationToken);
        var dtos = result.Items.Select(ConfigurationMapper.ToDto).ToList();

        var names = await _userDisplayNameResolver.ResolveAsync(
            dtos.SelectMany(dto => new[] { dto.CreatedBy, dto.ModifiedBy }), cancellationToken);
        var resolved = dtos.Select(dto => dto with
        {
            CreatedBy = dto.CreatedBy is { } createdBy ? names.GetValueOrDefault(createdBy, createdBy) : null,
            ModifiedBy = dto.ModifiedBy is { } modifiedBy ? names.GetValueOrDefault(modifiedBy, modifiedBy) : null,
        }).ToList();

        return new PagedResult<MappingProfileDto>(
            resolved,
            result.TotalCount,
            result.Page,
            result.PageSize);
    }

    public async Task<MappingProfileDto?> GetMappingProfileByIdAsync(Guid mappingProfileId, CancellationToken cancellationToken)
    {
        var mappingProfile = await _repository.GetMappingProfileAsync(mappingProfileId, cancellationToken);
        if (mappingProfile is null)
        {
            return null;
        }

        var dto = ConfigurationMapper.ToDto(mappingProfile);
        var names = await _userDisplayNameResolver.ResolveAsync(
            new[] { dto.CreatedBy, dto.ModifiedBy }, cancellationToken);

        return dto with
        {
            CreatedBy = dto.CreatedBy is { } createdBy ? names.GetValueOrDefault(createdBy, createdBy) : null,
            ModifiedBy = dto.ModifiedBy is { } modifiedBy ? names.GetValueOrDefault(modifiedBy, modifiedBy) : null,
        };
    }

    public async Task<int> GetMappingProfileUsageCountAsync(Guid mappingProfileId, CancellationToken cancellationToken)
    {
        var routes = await _repository.GetRoutesAsync(cancellationToken);

        return routes.Count(route =>
            route.MappingProfileId == mappingProfileId ||
            route.ResourceMappings.Any(mapping =>
                mapping.MappingProfileId == mappingProfileId ||
                mapping.ParentReferences.Any(parent => parent.ParentMappingProfileId == mappingProfileId)));
    }

    public async Task DeleteMappingProfileAsync(Guid mappingProfileId, CancellationToken cancellationToken)
    {
        var mappingProfile = await GetMappingProfileRequiredAsync(mappingProfileId, cancellationToken);
        await _repository.RemoveMappingProfileAsync(mappingProfile, cancellationToken);
    }

    public async Task<ResourceConfigurationDto> ConfigureResourceAsync(
        ConfigureResourceRequest request,
        CancellationToken cancellationToken)
    {
        // License workflow-quota and resource-type-allow-list enforcement live centrally in
        // LicenseEnforcementSaveChangesInterceptor (watches for a newly-Added ResourcePipelineRoute row and
        // resolves its resource type through the mapping profile itself).
        // A route's resource type, source, and destination are all owned by the mapping profile.
        var route = new ResourcePipelineRoute(
            request.WebhookConfigurationId,
            request.MappingProfileId,
            request.IngestionMode,
            request.ScheduleExpression,
            request.SearchParameters,
            request.IsEnabled,
            priority: 0,
            request.TimeZoneId);

        await _repository.AddRouteAsync(route, cancellationToken);

        var resourceType = await ResolveResourceTypeAsync(route, cancellationToken) ?? "Unknown";

        return await BuildResourceGroupDtoAsync(resourceType, cancellationToken);
    }

    public async Task<ResourceConfigurationDto> SetResourceConfigurationEnabledAsync(
        string resourceType,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var routes = await GetRoutesForResourceTypeAsync(resourceType, cancellationToken);
        foreach (var route in routes)
        {
            route.SetEnabled(isEnabled);
            await _repository.UpdateRouteAsync(route, cancellationToken);
        }

        return await BuildResourceGroupDtoAsync(resourceType, cancellationToken);
    }

    public async Task<ResourcePipelineRouteDto> AddResourceRouteAsync(
        string resourceType,
        CreateResourceRouteRequest request,
        CancellationToken cancellationToken)
    {
        // License workflow-quota and resource-type-allow-list enforcement live centrally in
        // LicenseEnforcementSaveChangesInterceptor — see ConfigureResourceAsync's matching comment.
        // resourceType path segment is ignored; the route's resource type, source, and destination come from its mapping.
        var route = new ResourcePipelineRoute(
            request.WebhookConfigurationId,
            request.MappingProfileId,
            request.IngestionMode,
            request.ScheduleExpression,
            request.SearchParameters,
            request.IsEnabled,
            request.Priority,
            request.TimeZoneId);
        await ApplyResourceMappingsAsync(route, request, cancellationToken);

        await _repository.AddRouteAsync(route, cancellationToken);

        // TimeZoneId is logged at creation because it is the value the scheduler will evaluate this route's cron
        // in, and a zone the host cannot resolve degrades silently to UTC at run time (see ScheduleEvaluationService).
        _logger.LogInformation(
            LogEvents.RouteSaved,
            "Pipeline route {RouteId} created for {ResourceType}: MappingProfileId={MappingProfileId} " +
            "IngestionMode={IngestionMode} Schedule={ScheduleExpression} TimeZoneId={TimeZoneId} " +
            "IsEnabled={IsEnabled} Priority={Priority}",
            route.Id, resourceType, route.MappingProfileId, route.IngestionMode,
            route.ScheduleExpression, route.TimeZoneId, route.IsEnabled, route.Priority);

        return ConfigurationMapper.ToDto(route);
    }

    public async Task<ResourcePipelineRouteDto> UpdateResourceRouteAsync(
        string resourceType,
        Guid routeId,
        CreateResourceRouteRequest request,
        CancellationToken cancellationToken)
    {
        var route = await GetRouteRequiredAsync(routeId, cancellationToken);
        route.Update(
            request.IngestionMode,
            request.WebhookConfigurationId,
            request.MappingProfileId,
            request.ScheduleExpression,
            request.SearchParameters,
            request.IsEnabled,
            request.Priority,
            request.TimeZoneId);
        await ApplyResourceMappingsAsync(route, request, cancellationToken);

        await _repository.UpdateRouteAsync(route, cancellationToken);

        return ConfigurationMapper.ToDto(route);
    }

    public async Task<ResourcePipelineRouteDto> SetResourceRouteEnabledAsync(
        string resourceType,
        Guid routeId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var route = await GetRouteRequiredAsync(routeId, cancellationToken);
        route.SetEnabled(isEnabled);

        await _repository.UpdateRouteAsync(route, cancellationToken);

        return ConfigurationMapper.ToDto(route);
    }

    // ── Route resource-type resolution (via mapping profile) ───────────────────

    private async Task ApplyResourceMappingsAsync(
        ResourcePipelineRoute route,
        CreateResourceRouteRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ResourceMappings is not { Count: > 0 })
        {
            route.ReplaceResourceMappings([]);
            return;
        }

        var primaryMapping = await GetMappingProfileRequiredAsync(request.MappingProfileId, cancellationToken);
        var normalizedMappings = new List<ResourcePipelineRouteMapping>();
        var seen = new HashSet<Guid>();
        var profilesInRoute = new Dictionary<Guid, MappingProfile> { [primaryMapping.Id] = primaryMapping };

        foreach (var mappingRequest in request.ResourceMappings)
        {
            if (!seen.Add(mappingRequest.MappingProfileId))
            {
                throw new InvalidOperationException(
                    "A resource mapping is listed more than once on this route.");
            }

            var mapping = await GetMappingProfileRequiredAsync(mappingRequest.MappingProfileId, cancellationToken);
            if (mapping.SourceConnectionId != primaryMapping.SourceConnectionId)
            {
                throw new InvalidOperationException(
                    "All resource mappings on a route must use mapping profiles from the same source connection.");
            }

            profilesInRoute[mapping.Id] = mapping;

            var routeMapping = new ResourcePipelineRouteMapping(
                mappingRequest.MappingProfileId,
                mappingRequest.IsEnabled,
                mappingRequest.ExecutionOrder,
                mappingRequest.SearchParameters);

            if (mappingRequest.ParentReferences is { Count: > 0 })
            {
                routeMapping.ReplaceParentReferences(mappingRequest.ParentReferences
                    .Select(p => new ParentReferenceLink(p.ParentMappingProfileId, p.ReferenceFieldOverride)));
            }

            normalizedMappings.Add(routeMapping);
        }

        if (!seen.Contains(request.MappingProfileId))
        {
            normalizedMappings.Add(new ResourcePipelineRouteMapping(
                request.MappingProfileId,
                isEnabled: true,
                executionOrder: 0));
        }

        ValidateParentReferences(normalizedMappings, profilesInRoute);

        route.ReplaceResourceMappings(normalizedMappings);
    }

    /// <summary>
    /// Enforces that every "child of" relationship declared on a route's resource mappings has its required
    /// FHIR reference field actually mapped — the save-time gate the parent-child mapping feature depends on.
    /// Runs regardless of whether the UI kept the mapping in sync, since a direct API call could otherwise
    /// bypass it.
    /// </summary>
    private void ValidateParentReferences(
        IReadOnlyCollection<ResourcePipelineRouteMapping> mappings,
        IReadOnlyDictionary<Guid, MappingProfile> profilesInRoute)
    {
        foreach (var routeMapping in mappings)
        {
            if (routeMapping.ParentReferences.Count == 0)
            {
                continue;
            }

            var childProfile = profilesInRoute[routeMapping.MappingProfileId];

            foreach (var link in routeMapping.ParentReferences)
            {
                if (!profilesInRoute.TryGetValue(link.ParentMappingProfileId, out var parentProfile))
                {
                    throw new InvalidOperationException(
                        $"'{childProfile.ResourceType}' is configured as a child of mapping profile " +
                        $"'{link.ParentMappingProfileId}', which is not part of this route.");
                }

                var requiredField = _parentReferenceResolver.Resolve(
                    childProfile.ResourceType, parentProfile.ResourceType, link.ReferenceFieldOverride);

                if (requiredField is null)
                {
                    throw new InvalidOperationException(
                        $"'{childProfile.ResourceType}' has no FHIR reference field that can target " +
                        $"'{parentProfile.ResourceType}'.");
                }

                var isMapped = childProfile.Fields.Any(f =>
                    f.IsEnabled && string.Equals(f.JsonPath, requiredField.JsonPath, StringComparison.Ordinal));

                if (!isMapped)
                {
                    throw new InvalidOperationException(
                        $"'{childProfile.ResourceType}' must map '{requiredField.FhirPath}' because it is " +
                        $"configured as a child of '{parentProfile.ResourceType}'.");
                }
            }
        }
    }

    private async Task<string?> ResolveResourceTypeAsync(ResourcePipelineRoute route, CancellationToken cancellationToken)
    {
        var mapping = await _repository.GetMappingProfileAsync(route.MappingProfileId, cancellationToken);
        return mapping?.ResourceType;
    }

    private async Task<IReadOnlyList<ResourcePipelineRoute>> GetRoutesForResourceTypeAsync(
        string resourceType,
        CancellationToken cancellationToken)
    {
        var routes = await _repository.GetRoutesAsync(cancellationToken);
        var mappings = (await _repository.GetMappingProfilesAsync(cancellationToken))
            .ToDictionary(x => x.Id);

        return routes
            .Where(route =>
                mappings.TryGetValue(route.MappingProfileId, out var mapping) &&
                string.Equals(mapping.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<ResourceConfigurationDto> BuildResourceGroupDtoAsync(
        string resourceType,
        CancellationToken cancellationToken)
    {
        var routes = await GetRoutesForResourceTypeAsync(resourceType, cancellationToken);

        return new ResourceConfigurationDto(
            Guid.Empty,
            resourceType,
            routes.Any(route => route.IsEnabled),
            routes
                .OrderBy(route => route.Priority)
                .ThenBy(route => route.Id)
                .Select(ConfigurationMapper.ToDto)
                .ToList());
    }

    // ── Required getters ───────────────────────────────────────────────────────

    private async Task<SourceConnection> GetSourceConnectionRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetSourceConnectionAsync(id, cancellationToken)
        ?? throw new NotFoundException("SourceConnection", id);

    private async Task<WebhookConfiguration> GetWebhookRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetWebhookAsync(id, cancellationToken)
        ?? throw new NotFoundException("WebhookConfiguration", id);

    private async Task<DestinationConfiguration> GetDestinationRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetDestinationAsync(id, cancellationToken)
        ?? throw new NotFoundException("DestinationConfiguration", id);

    private async Task<MappingProfile> GetMappingProfileRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetMappingProfileAsync(id, cancellationToken)
        ?? throw new NotFoundException("MappingProfile", id);

    private async Task<ResourcePipelineRoute> GetRouteRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetRouteAsync(id, cancellationToken)
        ?? throw new NotFoundException("ResourcePipelineRoute", id);

    /// <summary>
    /// Hard-blocks saving a mapping whose FHIR resource type the chosen source cannot provide, per its discovered
    /// capability profile. When no snapshot exists yet, discovery is run on demand for sources that support it (Epic);
    /// if the source type has no discovery support, we cannot prove the resource type is invalid and so fail open.
    /// </summary>
    private async Task EnsureSourceSupportsResourceTypeAsync(
        Guid sourceConnectionId,
        string resourceType,
        CancellationToken cancellationToken)
    {
        var capability = await _capabilityRepository.GetBySourceConnectionIdAsync(sourceConnectionId, cancellationToken);

        if (capability is null)
        {
            var sourceConnection = await _repository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
            if (sourceConnection is null || sourceConnection.SourceSystemType != SourceSystemType.Epic)
            {
                // Discovery is not implemented for this source type, so we cannot prove the resource type is
                // unsupported — fail open rather than lock authors out.
                return;
            }

            // Capability discovery calls /metadata with a SMART Backend Services token (client_credentials +
            // private_key_jwt). Interactive sources (EHR launch / provider standalone / patient) authenticate as a
            // user via authorization_code and have no private key at configuration time, so discovery cannot run
            // until a user completes a launch. Fail open for them rather than block authoring the mapping.
            var isInteractive = sourceConnection.ApplicationType is ApplicationType.EhrLaunch
                or ApplicationType.Standalone
                or ApplicationType.Patient;
            if (isInteractive)
            {
                return;
            }

            try
            {
                await _capabilityDiscoveryService.DiscoverAsync(sourceConnectionId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Discovery needs a working connection to the source — for Epic Backend Services, a successful
                // SMART token exchange. A brand-new connection whose key Epic hasn't been told about yet (or any
                // other transient reachability/auth failure) can't satisfy that, and failing the whole save here
                // would roll back the mapping AND the source connection this same request just created (both
                // committed together — see WorkflowEndpoints' build transaction), leaving the caller with no
                // saved connection and no id to register a JWKS URL against. Fail open exactly like the
                // "no discovery support" branch above: the resource type is left unverified for this save rather
                // than blocking it outright.
                _logger.LogWarning(
                    ex,
                    "Capability discovery failed for source connection {SourceConnectionId}; resource type " +
                    "'{ResourceType}' left unverified for this save.",
                    sourceConnectionId,
                    resourceType);
                return;
            }

            capability = await _capabilityRepository.GetBySourceConnectionIdAsync(sourceConnectionId, cancellationToken);

            if (capability is null)
            {
                return;
            }
        }

        if (!capability.SupportsResourceType(resourceType))
        {
            throw new InvalidOperationException(
                $"This source doesn't support the '{resourceType}' resource type. " +
                "Refresh its capabilities or choose a different resource type.");
        }
    }

    private async Task ValidateSourceConnectionRequestAsync(
        CreateSourceConnectionRequest request,
        Guid? excludeId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("Source connection name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            throw new InvalidOperationException("Source FHIR base URL is required.");
        }

        if (await _repository.ExistsWithNameAsync(request.Name, excludeId, cancellationToken))
        {
            throw new InvalidOperationException($"A source connection named '{request.Name}' already exists.");
        }

        if (request.SourceSystemType == SourceSystemType.Epic)
        {
            ValidateEpicSourceConnection(request);
        }

        // Retrieval config is a Backend System / Provider Standalone concept (vendor-agnostic — Epic, Cerner, or
        // any other source) independent of the Epic-specific checks above. Backend System gets the full method
        // picker for unattended, recurring execution; Provider Standalone gets a curated Search REST subset (no
        // Run Mode/scheduler — a one-shot, user-initiated fetch has no recurring run to schedule) — the DTO simply
        // arrives with those fields null for Standalone, which every check below already tolerates since they're
        // optional. EHR-launch / patient never populate it, so this is a no-op for them.
        if (request.ApplicationType is ApplicationType.Backend or ApplicationType.Standalone && request.Retrieval is not null)
        {
            ValidateRetrievalConfiguration(request.Retrieval);
        }
    }

    private static void ValidateRetrievalConfiguration(SourceRetrievalConfigurationDto retrieval)
    {
        if (string.IsNullOrWhiteSpace(retrieval.RetrievalMethod))
        {
            throw new InvalidOperationException("A data retrieval method is required for Backend System or Provider Standalone sources.");
        }

        if (retrieval.RetrievalMethod == "search-rest" && (retrieval.ResourceTypes is null || retrieval.ResourceTypes.Length == 0))
        {
            throw new InvalidOperationException("At least one resource type is required for Search (REST) retrieval.");
        }

        if (retrieval.PageSize is <= 0)
        {
            throw new InvalidOperationException("Page size must be a positive number.");
        }

        if (retrieval.TimeoutSeconds is <= 0)
        {
            throw new InvalidOperationException("Timeout must be a positive number of seconds.");
        }

        if (retrieval.MaxRecordsPerRun is <= 0)
        {
            throw new InvalidOperationException("Max records per run must be a positive number.");
        }
    }

    private static void ValidateEpicSourceConnection(CreateSourceConnectionRequest request)
    {
        ValidateHttpsUrl(request.BaseUrl, "Epic FHIR base URL");

        if (string.IsNullOrWhiteSpace(request.Authentication.ClientId))
        {
            throw new InvalidOperationException("Epic client id is required.");
        }

        if (request.Authentication.Scopes is null || request.Authentication.Scopes.Length == 0)
        {
            throw new InvalidOperationException("Epic scopes are required.");
        }

        // Interactive provider flows (EHR launch / provider standalone / patient) authenticate via
        // authorization_code + PKCE, selected by ApplicationType. Their authorize/token endpoints are discovered
        // from the source's .well-known/smart-configuration at runtime, so no token endpoint / KeyId / private key
        // is required at configuration time (the client may be public PKCE or confidential).
        var isInteractive = request.ApplicationType is ApplicationType.EhrLaunch
            or ApplicationType.Standalone
            or ApplicationType.Patient;

        if (isInteractive)
        {
            if (request.Interactive is null || request.Interactive.RedirectUris.Length == 0)
            {
                throw new InvalidOperationException("An interactive Epic source connection requires at least one redirect URI.");
            }

            if (request.ApplicationType == ApplicationType.EhrLaunch &&
                (request.Interactive.TrustedIssuers is null || request.Interactive.TrustedIssuers.Length == 0))
            {
                throw new InvalidOperationException("An Epic EHR-launch source connection requires at least one trusted issuer.");
            }

            return;
        }

        // A loopback base URL (e.g. docker-compose's local HAPI FHIR) is never a real Epic tenant — it has no OAuth
        // server to exchange a private_key_jwt assertion with, so the SMART Backend Services requirement below
        // would be unsatisfiable no matter what's configured. Skip it entirely for loopback; any real (non-loopback)
        // Epic endpoint still requires full JWT/Key Vault setup, unchanged.
        if (IsLoopbackUrl(request.BaseUrl))
        {
            return;
        }

        // Backend Services (machine-to-machine): client_credentials + private_key_jwt.
        if (request.Authentication.AuthenticationType != AuthenticationType.SmartBackendServices)
        {
            throw new InvalidOperationException("A Backend Services Epic source connection must use SMART Backend Services authentication.");
        }

        ValidateHttpsUrl(request.Authentication.TokenEndpoint, "Epic token endpoint");

        if (string.IsNullOrWhiteSpace(request.Authentication.KeyId))
        {
            throw new InvalidOperationException("Epic public key id is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Authentication.PrivateKeyKeyVaultName) ||
            string.IsNullOrWhiteSpace(request.Authentication.PrivateKeySecretName))
        {
            throw new InvalidOperationException("Epic private key must be stored as a secret reference.");
        }
    }

    private static bool IsLoopbackUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsLoopback;

    private static void ValidateHttpsUrl(string? value, string fieldName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"{fieldName} must be an absolute HTTPS URL.");
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Mirrors SourceConnection.ValidateBaseUrl's loopback exception, so a local/dev FHIR server (e.g.
        // docker-compose's HAPI FHIR at http://localhost:8080/fhir) can be configured without relaxing the HTTPS
        // requirement for real endpoints.
        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{fieldName} must be an absolute HTTPS URL (plain HTTP is allowed only for loopback addresses).");
    }
}
