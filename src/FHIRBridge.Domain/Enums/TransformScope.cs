namespace FHIRBridge.Domain.Enums;

/// <summary>
/// The fallback chain a <see cref="Entities.TransformationRule"/> is resolved at, most specific first:
/// Workflow overrides Field, Field overrides ResourceType, ResourceType overrides DestinationType,
/// DestinationType overrides Global. A field's effective rule set is whichever tier has at least one
/// matching row, walked from <see cref="Workflow"/> down to <see cref="Global"/>.
/// </summary>
public enum TransformScope
{
    Global = 0,
    DestinationType = 1,
    ResourceType = 2,
    Field = 3,
    Workflow = 4
}
