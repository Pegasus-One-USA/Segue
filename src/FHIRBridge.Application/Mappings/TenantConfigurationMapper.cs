using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Aggregates;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Application.Mappings;

public static class TenantConfigurationMapper
{
    public static TenantConfigurationDto ToDto(Tenant tenant)
    {
        return new TenantConfigurationDto(
            tenant.Id,
            tenant.Name,
            tenant.Code,
            tenant.Status,
            tenant.SourceConnections.Select(ToDto).ToList(),
            tenant.WebhookConfigurations.Select(ToDto).ToList(),
            tenant.DestinationConfigurations.Select(ToDto).ToList(),
            tenant.MappingProfiles.Select(ToDto).ToList(),
            ToResourceGroupDtos(tenant));
    }

    /// <summary>
    /// Builds the resource-grouped read model from routes. Resource type is derived from each route's mapping
    /// profile (the single source of truth) — there is no ResourceConfiguration entity. The group <c>Id</c> is
    /// <see cref="Guid.Empty"/> because the grouping is computed, not stored.
    /// </summary>
    public static IReadOnlyList<ResourceConfigurationDto> ToResourceGroupDtos(Tenant tenant)
    {
        return tenant.ResourcePipelineRoutes
            .GroupBy(route => tenant.ResolveResourceType(route) ?? "Unknown", StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ResourceConfigurationDto(
                Guid.Empty,
                group.Key,
                group.Any(route => route.IsEnabled),
                group
                    .OrderBy(route => route.Priority)
                    .ThenBy(route => route.Id)
                    .Select(ToDto)
                    .ToList()))
            .ToList();
    }

    /// <summary>The derived resource group for a single resource type (used by single-resource responses).</summary>
    public static ResourceConfigurationDto ToResourceGroupDto(Tenant tenant, string resourceType)
    {
        return ToResourceGroupDtos(tenant)
            .FirstOrDefault(group => string.Equals(group.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase))
            ?? new ResourceConfigurationDto(Guid.Empty, resourceType, false, []);
    }

    public static SourceConnectionDto ToDto(SourceConnection sourceConnection)
    {
        return new SourceConnectionDto(
            sourceConnection.Id,
            sourceConnection.Name,
            sourceConnection.SourceSystemType,
            sourceConnection.BaseUrl,
            new SourceAuthenticationDto(
                sourceConnection.Authentication.AuthenticationType,
                sourceConnection.Authentication.ClientId,
                sourceConnection.Authentication.TokenEndpoint,
                sourceConnection.Authentication.Scopes,
                sourceConnection.Authentication.ClientSecret?.KeyVaultName,
                sourceConnection.Authentication.ClientSecret?.SecretName,
                sourceConnection.Authentication.PrivateKey?.KeyVaultName,
                sourceConnection.Authentication.PrivateKey?.SecretName,
                sourceConnection.Authentication.KeyId),
            sourceConnection.IsEnabled);
    }

    public static WebhookConfigurationDto ToDto(WebhookConfiguration webhookConfiguration)
    {
        return new WebhookConfigurationDto(
            webhookConfiguration.Id,
            webhookConfiguration.SourceConnectionId,
            webhookConfiguration.ResourceType,
            webhookConfiguration.Name,
            webhookConfiguration.Path,
            webhookConfiguration.IsEnabled);
    }

    public static DestinationConfigurationDto ToDto(DestinationConfiguration destinationConfiguration)
    {
        return new DestinationConfigurationDto(
            destinationConfiguration.Id,
            destinationConfiguration.Name,
            destinationConfiguration.DestinationType,
            destinationConfiguration.SecretReference.KeyVaultName,
            destinationConfiguration.SecretReference.SecretName,
            destinationConfiguration.Target,
            destinationConfiguration.IsEnabled);
    }

    public static MappingProfileDto ToDto(MappingProfile mappingProfile)
    {
        return new MappingProfileDto(
            mappingProfile.Id,
            mappingProfile.Name,
            mappingProfile.ResourceType,
            mappingProfile.SourceConnectionId,
            mappingProfile.DestinationId,
            mappingProfile.DestinationObject,
            mappingProfile.Fields
                .Select(ToDto)
                .ToList(),
            mappingProfile.IsEnabled);
    }

    public static MappingFieldDto ToDto(MappingField field)
    {
        return new MappingFieldDto(
            field.TargetField,
            field.JsonPath,
            field.ValueType,
            field.IsRequired,
            field.DefaultValue,
            field.Format,
            field.ResourceType,
            field.DestinationObject,
            field.NormalizationType,
            field.TerminologySystemJsonPath,
            field.TerminologyCodeJsonPath,
            field.ArrayPolicy,
            field.Cardinality,
            string.IsNullOrWhiteSpace(field.ArrayAncestors)
                ? null
                : field.ArrayAncestors.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public static ResourcePipelineRouteDto ToDto(ResourcePipelineRoute route)
    {
        return new ResourcePipelineRouteDto(
            route.Id,
            route.WebhookConfigurationId,
            route.MappingProfileId,
            route.IngestionMode,
            route.ScheduleExpression,
            route.SearchParameters,
            route.IsEnabled,
            route.Priority);
    }

    public static MappingField ToDomain(MappingFieldDto dto)
    {
        return new MappingField(
            dto.TargetField,
            dto.JsonPath,
            dto.ValueType,
            dto.IsRequired,
            dto.DefaultValue,
            dto.Format,
            dto.ResourceType,
            dto.DestinationObject,
            dto.NormalizationType,
            dto.TerminologySystemJsonPath,
            dto.TerminologyCodeJsonPath,
            IsEnabled: true,
            ArrayPolicy: dto.ArrayPolicy,
            Cardinality: dto.Cardinality,
            ArrayAncestors: dto.ArrayAncestors is { Count: > 0 } ? string.Join('|', dto.ArrayAncestors) : null);
    }

    public static SourceAuthenticationConfiguration ToDomain(SourceAuthenticationDto dto)
    {
        return new SourceAuthenticationConfiguration(
            dto.AuthenticationType,
            dto.ClientId,
            dto.TokenEndpoint,
            dto.Scopes ?? [],
            CreateSecretReference(dto.ClientSecretKeyVaultName, dto.ClientSecretName),
            CreateSecretReference(dto.PrivateKeyKeyVaultName, dto.PrivateKeySecretName),
            dto.KeyId);
    }

    private static SecretReference? CreateSecretReference(string? keyVaultName, string? secretName)
    {
        return string.IsNullOrWhiteSpace(keyVaultName) || string.IsNullOrWhiteSpace(secretName)
            ? null
            : new SecretReference(keyVaultName, secretName);
    }
}
