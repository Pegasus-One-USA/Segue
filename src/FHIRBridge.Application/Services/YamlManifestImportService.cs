using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Manifests;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FHIRBridge.Application.Services;

/// <inheritdoc />
public sealed class YamlManifestImportService : IYamlManifestImportService
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithCaseInsensitivePropertyMatching()
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly IUnifiedTenantConfigurationService _tenantConfigurationService;

    public YamlManifestImportService(IUnifiedTenantConfigurationService tenantConfigurationService)
    {
        _tenantConfigurationService = tenantConfigurationService;
    }

    public async Task<ManifestImportResultDto> ImportAsync(string yamlContent, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(yamlContent))
        {
            throw new InvalidOperationException("The manifest is empty.");
        }

        var manifest = Parse(yamlContent);
        Validate(manifest);

        var warnings = new List<string>();

        var tenant = await _tenantConfigurationService.CreateTenantAsync(
            new CreateTenantRequest(manifest.Tenant.Name.Trim(), manifest.Tenant.Code.Trim()),
            cancellationToken);

        var sourceIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in manifest.Sources)
        {
            var created = await _tenantConfigurationService.AddSourceConnectionAsync(
                tenant.Id,
                new CreateSourceConnectionRequest(
                    source.Name,
                    source.SystemType,
                    source.BaseUrl,
                    new SourceAuthenticationDto(
                        source.Authentication.Type,
                        source.Authentication.ClientId,
                        source.Authentication.TokenEndpoint,
                        [.. source.Authentication.Scopes],
                        source.Authentication.ClientSecretKeyVaultName,
                        source.Authentication.ClientSecretName,
                        source.Authentication.PrivateKeyKeyVaultName,
                        source.Authentication.PrivateKeySecretName,
                        source.Authentication.KeyId)),
                cancellationToken);
            sourceIds[source.Name] = created.Id;
        }

        var destinationIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var destination in manifest.Destinations)
        {
            var created = await _tenantConfigurationService.AddDestinationConfigurationAsync(
                tenant.Id,
                new CreateDestinationConfigurationRequest(
                    destination.Name,
                    destination.Type,
                    destination.KeyVaultName,
                    destination.SecretName,
                    destination.Target),
                cancellationToken);
            destinationIds[destination.Name] = created.Id;
        }

        var webhookIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var webhook in manifest.Webhooks)
        {
            var created = await _tenantConfigurationService.AddWebhookConfigurationAsync(
                tenant.Id,
                new CreateWebhookConfigurationRequest(
                    sourceIds[webhook.Source],
                    webhook.ResourceType,
                    webhook.Name,
                    webhook.Path,
                    webhook.IsEnabled),
                cancellationToken);
            webhookIds[webhook.Name] = created.Id;
        }

        var mappingIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in manifest.Mappings)
        {
            var created = await _tenantConfigurationService.AddMappingProfileAsync(
                tenant.Id,
                new CreateMappingProfileRequest(
                    mapping.Name,
                    mapping.ResourceType,
                    sourceIds[mapping.Source],
                    destinationIds[mapping.Destination],
                    mapping.DestinationObject,
                    [.. mapping.Fields.Select(field => new MappingFieldDto(
                        field.TargetField,
                        field.JsonPath,
                        field.ValueType,
                        field.IsRequired,
                        field.DefaultValue,
                        field.Format,
                        NormalizationType: field.NormalizationType,
                        TerminologySystemJsonPath: field.TerminologySystemJsonPath,
                        TerminologyCodeJsonPath: field.TerminologyCodeJsonPath))]),
                cancellationToken);
            mappingIds[mapping.Name] = created.Id;
        }

        var routeCount = 0;
        foreach (var resource in manifest.Resources)
        {
            await _tenantConfigurationService.ConfigureResourceAsync(
                tenant.Id,
                new ConfigureResourceRequest(
                    resource.IsEnabled,
                    resource.IngestionMode,
                    ResolveWebhook(resource.Webhook, webhookIds),
                    mappingIds[resource.Mapping],
                    resource.ScheduleExpression,
                    resource.SearchParameters),
                cancellationToken);
            routeCount++;

            foreach (var route in resource.AdditionalRoutes)
            {
                await _tenantConfigurationService.AddResourceRouteAsync(
                    tenant.Id,
                    resource.ResourceType,
                    new CreateResourceRouteRequest(
                        route.IngestionMode,
                        ResolveWebhook(route.Webhook, webhookIds),
                        mappingIds[route.Mapping],
                        route.ScheduleExpression,
                        route.SearchParameters,
                        route.IsEnabled,
                        route.Priority),
                    cancellationToken);
                routeCount++;
            }
        }

        return new ManifestImportResultDto(
            tenant.Id,
            tenant.Name,
            tenant.Code,
            manifest.Sources.Count,
            manifest.Destinations.Count,
            manifest.Webhooks.Count,
            manifest.Mappings.Count,
            manifest.Resources.Count,
            routeCount,
            warnings);
    }

    private static TenantManifest Parse(string yamlContent)
    {
        try
        {
            return Deserializer.Deserialize<TenantManifest>(yamlContent)
                ?? throw new InvalidOperationException("The manifest did not contain any content.");
        }
        catch (YamlException exception)
        {
            var detail = exception.InnerException?.Message ?? exception.Message;
            throw new InvalidOperationException($"The manifest could not be parsed: {detail}", exception);
        }
    }

    private static Guid? ResolveWebhook(string? webhookName, IReadOnlyDictionary<string, Guid> webhookIds)
    {
        return string.IsNullOrWhiteSpace(webhookName) ? null : webhookIds[webhookName];
    }

    /// <summary>
    /// Verifies the manifest is internally consistent (required fields present, names unique, all references
    /// resolvable) before any entity is created, so a bad reference fails fast instead of leaving a half-built tenant.
    /// </summary>
    private static void Validate(TenantManifest manifest)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(manifest.Tenant.Name))
        {
            errors.Add("tenant.name is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Tenant.Code))
        {
            errors.Add("tenant.code is required.");
        }

        var sourceNames = CollectNames(manifest.Sources.Select(s => s.Name), "source", errors);
        var destinationNames = CollectNames(manifest.Destinations.Select(d => d.Name), "destination", errors);
        var mappingNames = CollectNames(manifest.Mappings.Select(m => m.Name), "mapping", errors);
        var webhookNames = CollectNames(manifest.Webhooks.Select(w => w.Name), "webhook", errors);

        foreach (var webhook in manifest.Webhooks)
        {
            RequireReference(webhook.Source, sourceNames, "source", $"webhook '{webhook.Name}'", errors);
        }

        foreach (var mapping in manifest.Mappings)
        {
            RequireReference(mapping.Source, sourceNames, "source", $"mapping '{mapping.Name}'", errors);
            RequireReference(mapping.Destination, destinationNames, "destination", $"mapping '{mapping.Name}'", errors);
        }

        foreach (var resource in manifest.Resources)
        {
            var label = $"resource '{resource.ResourceType}'";
            RequireReference(resource.Mapping, mappingNames, "mapping", label, errors);
            RequireOptionalReference(resource.Webhook, webhookNames, "webhook", label, errors);

            foreach (var route in resource.AdditionalRoutes)
            {
                var routeLabel = $"{label} additional route";
                RequireReference(route.Mapping, mappingNames, "mapping", routeLabel, errors);
                RequireOptionalReference(route.Webhook, webhookNames, "webhook", routeLabel, errors);
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"The manifest is invalid: {string.Join(" ", errors)}");
        }
    }

    private static HashSet<string> CollectNames(IEnumerable<string> names, string kind, List<string> errors)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"Every {kind} must have a name.");
                continue;
            }

            if (!unique.Add(name))
            {
                errors.Add($"Duplicate {kind} name '{name}'.");
            }
        }

        return unique;
    }

    private static void RequireReference(
        string reference,
        HashSet<string> declared,
        string kind,
        string owner,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            errors.Add($"{owner} must reference a {kind}.");
        }
        else if (!declared.Contains(reference))
        {
            errors.Add($"{owner} references unknown {kind} '{reference}'.");
        }
    }

    private static void RequireOptionalReference(
        string? reference,
        HashSet<string> declared,
        string kind,
        string owner,
        List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(reference) && !declared.Contains(reference))
        {
            errors.Add($"{owner} references unknown {kind} '{reference}'.");
        }
    }
}
