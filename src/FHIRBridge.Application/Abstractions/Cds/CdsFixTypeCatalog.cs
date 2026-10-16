using FHIRBridge.Application.DTOs.Cds;

namespace FHIRBridge.Application.Abstractions.Cds;

/// <summary>
/// Descriptors of the built-in fix types, so every handler describes the same fix the same way. A handler passes
/// its own trigger options (e.g. the drug classes or care gaps its rules know).
/// </summary>
public static class CdsFixTypeCatalog
{
    private static readonly CdsFixMappingField TriggerField =
        new(CdsFixMappingFieldKeys.Trigger, "Applies to", false, "Leave empty to use this code for every case.");

    private static readonly CdsFixMappingField SystemField = new(CdsFixMappingFieldKeys.System, "Code system", true);

    public static readonly IReadOnlyList<CdsSettingOption> MedicationCodeSystems =
    [
        new(CdsFixCodeSystems.EpicPreferenceListItem, "Epic preference list item"),
        new(CdsFixCodeSystems.RxNormOid, "RxNorm"),
        new(CdsFixCodeSystems.Ndc, "NDC"),
    ];

    public static readonly IReadOnlyList<CdsSettingOption> OrderCodeSystems =
    [
        new(CdsFixCodeSystems.EpicPreferenceListItem, "Epic preference list item"),
        new(CdsFixCodeSystems.EpicOrderSetItem, "Epic order set item"),
    ];

    public static readonly IReadOnlyList<CdsSettingOption> ConditionCodeSystems =
    [
        new(CdsFixCodeSystems.Icd10Cm, "ICD-10-CM"),
        new(CdsFixCodeSystems.EpicCmsHcc, "Epic CMS-HCC"),
        new(CdsFixCodeSystems.EpicHhsHcc, "Epic HHS-HCC"),
    ];

    /// <param name="resourceType">"MedicationRequest" or "ServiceRequest".</param>
    public static CdsFixTypeDescriptor RemoveDraftOrder(string resourceType = "MedicationRequest") =>
        new(
            CdsFixTypeKeys.RemoveDraftOrder,
            "Remove the draft order",
            CdsActionTypes.Delete,
            resourceType,
            [],
            RequiresMapping: false,
            Help: "Removes the order the card is about from the clinician's unsigned orders. Needs no code.");

    public static CdsFixTypeDescriptor AddAlternativeMedication(IReadOnlyList<CdsSettingOption>? triggers) =>
        new(
            CdsFixTypeKeys.AddAlternativeMedication,
            "Add an alternative medication",
            CdsActionTypes.Create,
            "MedicationRequest",
            [
                TriggerField,
                SystemField,
                new(CdsFixMappingFieldKeys.Code, "Preference list item or drug code", true),
                new(CdsFixMappingFieldKeys.Display, "Drug name", true),
            ],
            RequiresMapping: true,
            triggers,
            MedicationCodeSystems,
            "Adds a draft order for the alternative. Epic uses its own order defaults (dose, frequency) for it.");

    public static CdsFixTypeDescriptor AddTestOrder(IReadOnlyList<CdsSettingOption>? triggers) =>
        new(
            CdsFixTypeKeys.AddTestOrder,
            "Add the test or procedure order",
            CdsActionTypes.Create,
            "ServiceRequest",
            [
                TriggerField,
                SystemField,
                new(CdsFixMappingFieldKeys.Code, "Preference list or order set item", true),
                new(CdsFixMappingFieldKeys.Display, "Order name", false),
            ],
            RequiresMapping: true,
            triggers,
            OrderCodeSystems,
            "Adds a draft order. Use the health system's preference list item ids.");

    public static CdsFixTypeDescriptor AddProblem(IReadOnlyList<CdsSettingOption>? triggers) =>
        new(
            CdsFixTypeKeys.AddProblem,
            "Add to the problem list",
            CdsActionTypes.Create,
            "Condition",
            [
                TriggerField,
                SystemField,
                new(CdsFixMappingFieldKeys.Code, "Diagnosis code", true),
                new(CdsFixMappingFieldKeys.Display, "Diagnosis name", true),
            ],
            RequiresMapping: true,
            triggers,
            ConditionCodeSystems,
            "Adds a problem list entry (Condition, category problem-list-item).");

    public static CdsFixTypeDescriptor AddEncounterDiagnosis(IReadOnlyList<CdsSettingOption>? triggers) =>
        new(
            CdsFixTypeKeys.AddEncounterDiagnosis,
            "Add an encounter diagnosis",
            CdsActionTypes.Create,
            "Condition",
            [
                TriggerField,
                SystemField,
                new(CdsFixMappingFieldKeys.Code, "Diagnosis code", true),
                new(CdsFixMappingFieldKeys.Display, "Diagnosis name", true),
            ],
            RequiresMapping: true,
            triggers,
            ConditionCodeSystems,
            "Adds a diagnosis to the current encounter (Condition, category encounter-diagnosis). Offered only in an encounter.");
}
