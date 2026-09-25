namespace FHIRBridge.Application.DTOs;

/// <summary>One resource type's stored FHIR search criteria for a source node of a workflow.</summary>
public sealed record ResourceTypeCriteriaDto(
    Guid Id,
    Guid WorkflowId,
    string SourceNodeId,
    string ResourceType,
    string Criteria);

/// <summary>
/// Saves criteria for one resource type. <paramref name="Criteria"/> is raw FHIR search parameters
/// (<c>birthdate=gt2000-01-01&amp;gender=female</c>), never parsed or validated server-side — any parameter the
/// target FHIR server accepts is valid.
/// </summary>
/// <param name="Replace">
/// <c>false</c> (the default) appends to whatever is already stored, dropping parameters whose key is already
/// present — the "Criteria" button's append behavior. <c>true</c> overwrites, which is the only way to remove or
/// rewrite an existing parameter.
/// </param>
public sealed record SaveResourceTypeCriteriaRequest(
    string SourceNodeId,
    string ResourceType,
    string Criteria,
    bool Replace = false);
