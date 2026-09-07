namespace FHIRBridge.Domain.Enums;

/// <summary>
/// When a <see cref="Entities.TransformationRule"/> executes. <see cref="PostMapping"/> (default) is the
/// original behavior — the rule sees a value already assigned to a destination field/column.
/// <see cref="PreMapping"/> rules instead walk the raw source resource JSON by <c>SourceField</c> path, before
/// any field mapping happens — this is how field-level de-identification (Safe Harbor-style redaction) is
/// expressed, and it only applies at <see cref="TransformScope.Global"/>/<see cref="TransformScope.ResourceType"/>
/// scope, tagged with a <see cref="Entities.DeIdentificationProfile"/>.
/// <see cref="FhirResource"/> rules also walk the raw resource JSON, but for a different reason and with no
/// de-identification profile: a FHIR-native destination (Aidbox/Medplum/Azure FHIR) stores the resource itself
/// rather than mapped columns, so there is no destination field for a PostMapping rule to attach to. These are
/// keyed by <c>SourceField</c> (what to read) and <c>FhirWriteBackJsonPath</c> (where the result goes, defaulting
/// to the read path), and are executed by V2's own FhirResourceTransformNode — never inside a V1 pipeline.
/// </summary>
public enum TransformExecutionPhase
{
    PostMapping = 0,
    PreMapping = 1,
    FhirResource = 2
}
