using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

/// <summary>
/// Materializes a full tenant configuration from a declarative YAML manifest by invoking the existing tenant
/// configuration create operations. Name references inside the manifest are resolved to the identifiers assigned
/// as each entity is created.
/// </summary>
public interface IYamlManifestImportService
{
    /// <summary>
    /// Parses and applies the supplied YAML manifest, creating a new tenant and all declared sources, destinations,
    /// webhooks, mapping profiles and resource routes.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the manifest is empty, cannot be parsed, or contains invalid/unresolvable references.
    /// </exception>
    Task<ManifestImportResultDto> ImportAsync(string yamlContent, CancellationToken cancellationToken);
}
