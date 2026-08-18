namespace FHIRBridge.Domain.Enums;

/// <summary>
/// When a <see cref="Entities.TransformationRule"/> executes. <see cref="PostMapping"/> (default) is the
/// original behavior — the rule sees a value already assigned to a destination field/column.
/// <see cref="PreMapping"/> rules instead walk the raw source resource JSON by <c>SourceField</c> path, before
/// any field mapping happens — this is how field-level de-identification (Safe Harbor-style redaction) is
/// expressed, and it only applies at <see cref="TransformScope.Global"/>/<see cref="TransformScope.ResourceType"/>
/// scope, tagged with a <see cref="Entities.DeIdentificationProfile"/>.
/// </summary>
public enum TransformExecutionPhase
{
    PostMapping = 0,
    PreMapping = 1
}
