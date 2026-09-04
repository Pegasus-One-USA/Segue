using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.Fhir;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Route("api/v1/mapping")]
public sealed class MappingController : ControllerBase
{
    private readonly IJsonMappingEngine _mappingEngine;
    private readonly IFhirElementCatalog _genericCatalog;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly IServiceProvider _serviceProvider;

    public MappingController(
        IJsonMappingEngine mappingEngine,
        [FromKeyedServices(FhirElementCatalogKeys.Generic)] IFhirElementCatalog genericCatalog,
        IConfigurationRepository configurationRepository,
        IServiceProvider serviceProvider)
    {
        _mappingEngine = mappingEngine;
        _genericCatalog = genericCatalog;
        _configurationRepository = configurationRepository;
        _serviceProvider = serviceProvider;
    }

    [HttpPost("test")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(MappingTestResultDto), StatusCodes.Status200OK)]
    public IActionResult TestMapping([FromBody] TestMappingRequest request)
    {
        var result = _mappingEngine.Map(request.SourceJson, request.Fields);

        return Ok(result);
    }

    // Deliberately still backed by the generic catalog only, even for Epic sources: this is the
    // resource-TYPE picker (Patient, Condition, ...), and only Patient has an Epic-specific catalog so
    // far (see fhir-r4-catalog.epic.json). Making this vendor-aware for field CATALOG selection today
    // would narrow an Epic source's resource picker down to just "Patient" until every resource gets its
    // own Epic template — a regression versus what it shows now. Field-level lookup (below) is the one
    // that's vendor-aware, with a fallback to generic per resource type, so this can follow once more
    // resources are converted.
    //
    // The optional `vendor` query param is a different, narrower concern: which of these resource TYPES
    // a vendor's live FHIR server is actually known to support at all (VendorResourceTypeSupport), not
    // which have a richer field catalog. A vendor absent from that list (Epic included) gets no filter —
    // same full response as before this param existed.
    [HttpGet("catalog/resources")]
    [Authorize(Policy = AuthorizationPolicies.MappingCatalogAccess)]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    public IActionResult GetCatalogResources([FromQuery] string? vendor)
    {
        var supported = VendorResourceTypeSupport.For(vendor);
        if (supported is null)
        {
            return Ok(_genericCatalog.ResourceTypes);
        }

        var supportedSet = new HashSet<string>(supported, StringComparer.OrdinalIgnoreCase);
        var filtered = _genericCatalog.ResourceTypes.Where(supportedSet.Contains).ToList();
        return Ok(filtered);
    }

    /// <summary>
    /// Returns the field catalog for a resource type. When <paramref name="sourceConnectionId"/> is
    /// supplied, resolves that source's vendor and prefers its vendor-specific catalog (Epic today).
    /// <paramref name="sourceVendor"/> is the fallback for a source node that hasn't been saved yet (no
    /// real connection id assigned) but already has a vendor picked in its own form (e.g. the Epic
    /// wizard's EHR selector defaults to "Epic" from the moment it's dropped on the canvas) — without
    /// this, a brand-new Epic source would show the generic catalog until the first save round-trip.
    /// Falls back to the generic base-FHIR-R4 catalog for any resource type the vendor catalog doesn't
    /// cover yet, or when neither a source connection nor a vendor is given at all.
    /// </summary>
    [HttpGet("catalog/resources/{resourceType}/fields")]
    [Authorize(Policy = AuthorizationPolicies.MappingCatalogAccess)]
    [ProducesResponseType(typeof(IReadOnlyList<FhirElementDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCatalogFields(
        string resourceType,
        [FromQuery] Guid? sourceConnectionId,
        [FromQuery] string? sourceVendor,
        CancellationToken cancellationToken)
    {
        var catalog = await ResolveCatalogAsync(sourceConnectionId, sourceVendor, cancellationToken);
        var fields = catalog.Fields(resourceType);

        if (fields.Count == 0 && !ReferenceEquals(catalog, _genericCatalog))
        {
            fields = _genericCatalog.Fields(resourceType);
        }

        if (fields.Count == 0)
        {
            return NotFound();
        }

        if (!ReferenceEquals(catalog, _genericCatalog))
        {
            fields = FillMissingReferenceTargetTypes(fields, resourceType);
        }

        return Ok(fields);
    }

    /// <summary>
    /// A vendor-specific catalog (Epic today) doesn't always carry <see cref="FhirElementDto.ReferenceTargetTypes"/>
    /// for a reference field even though FHIR's Reference semantics for that field (e.g. Observation.subject can
    /// target Patient/Group/Device/Location) don't actually vary by vendor - only which fields/profiles the
    /// vendor catalog happens to document does. Backfills any gap from the generic R4 catalog by FhirPath, so a
    /// vendor source doesn't silently lose the mapping UI's "which resource does this reference?" auto-detection
    /// (see the portal's ResourceFieldDef.referenceTargetTypes / DestinationWizardComponent.buildParentReferenceWarnings).
    /// </summary>
    private IReadOnlyList<FhirElementDto> FillMissingReferenceTargetTypes(
        IReadOnlyList<FhirElementDto> fields, string resourceType)
    {
        var genericFields = _genericCatalog.Fields(resourceType);
        if (genericFields.Count == 0)
        {
            return fields;
        }

        var genericByPath = genericFields.ToDictionary(f => f.FhirPath, StringComparer.Ordinal);

        return fields
            .Select(f => f.ReferenceTargetTypes.Count == 0
                    && f.FhirPath.EndsWith(".reference", StringComparison.Ordinal)
                    && genericByPath.TryGetValue(f.FhirPath, out var generic)
                    && generic.ReferenceTargetTypes.Count > 0
                ? f with { ReferenceTargetTypes = generic.ReferenceTargetTypes }
                : f)
            .ToList();
    }

    private async Task<IFhirElementCatalog> ResolveCatalogAsync(
        Guid? sourceConnectionId,
        string? sourceVendor,
        CancellationToken cancellationToken)
    {
        if (sourceConnectionId is not null)
        {
            var source = await _configurationRepository.GetSourceConnectionAsync(sourceConnectionId.Value, cancellationToken);
            if (source is not null)
            {
                return _serviceProvider.GetRequiredKeyedService<IFhirElementCatalog>(FhirElementCatalogKeys.For(source.SourceSystemType));
            }
        }

        if (!string.IsNullOrWhiteSpace(sourceVendor) && Enum.TryParse<SourceSystemType>(sourceVendor, ignoreCase: true, out var vendor))
        {
            return _serviceProvider.GetRequiredKeyedService<IFhirElementCatalog>(FhirElementCatalogKeys.For(vendor));
        }

        return _genericCatalog;
    }
}
