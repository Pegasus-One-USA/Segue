using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Abstractions.Mapping;

/// <summary>
/// Keyed-DI keys for the vendor-specific <see cref="IFhirElementCatalog"/> registrations (see
/// DependencyInjection.cs). Only Epic has its own catalog today (fhir-r4-catalog.epic.json, generated
/// from Epic profile templates by tools/EpicTemplateCatalogGenerator) — every other vendor still reads
/// the generic base-FHIR-R4 catalog until it gets its own.
/// </summary>
public static class FhirElementCatalogKeys
{
    public const string Generic = "Generic";
    public const string Epic = "Epic";

    public static string For(SourceSystemType vendor) => vendor == SourceSystemType.Epic ? Epic : Generic;
}
