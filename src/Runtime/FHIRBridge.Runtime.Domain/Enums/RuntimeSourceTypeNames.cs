namespace FHIRBridge.Runtime.Domain.Enums;

/// <summary>
/// The vendor's own name for a <see cref="RuntimeSourceType"/>, for log lines, error messages and audit text.
/// <para>
/// Several vendors deliberately share one code path — the SMART Backend Services token provider serves every
/// vendor whose app is registered with a private key, and the shared REST connector serves every vendor with no
/// confirmed request-level quirks of its own — so those types cannot name a vendor in a message from their own
/// class name. They resolve it from the source configuration's <see cref="RuntimeSourceType"/> through here
/// instead, which is why an athenahealth or eClinicalWorks failure no longer reads as an Epic one.
/// </para>
/// A plain name lookup, not behaviour: vendor BEHAVIOUR stays on the connector inheritance axis (a subclass of
/// <c>FhirSourceConnectorBase</c>) and the application-type strategy registry, never on a switch here.
/// </summary>
public static class RuntimeSourceTypeNames
{
    public static string DisplayName(RuntimeSourceType sourceType) => sourceType switch
    {
        RuntimeSourceType.Epic => "Epic",
        RuntimeSourceType.Cerner => "Cerner",
        RuntimeSourceType.Allscripts => "Allscripts",
        // Lower-cased deliberately: athenahealth styles its own name that way, and AthenahealthFhirSourceClient's
        // SourceDisplayName ("athenahealth FHIR") already matches, so both agree in the same log.
        RuntimeSourceType.Athenahealth => "athenahealth",
        // Healow is the enum member; eClinicalWorks is what the product is called everywhere a user can see it.
        RuntimeSourceType.Healow => "eClinicalWorks",
        RuntimeSourceType.MeditechGreenfield => "MEDITECH Greenfield",
        RuntimeSourceType.GenericFhir => "FHIR",
        RuntimeSourceType.Sample => "Sample FHIR",
        _ => sourceType.ToString(),
    };
}
