namespace FHIRBridge.Domain.Enums;

/// <summary>
/// How the patient context is established for an interactive (standalone) source connection. Determines the patient
/// scope requested at authorize time and how the patient is resolved after sign-in.
/// </summary>
public enum PatientSelectionMethod
{
    /// <summary>The EHR presents its patient picker at login (SMART <c>launch/patient</c> scope); the chosen patient is returned in the token response.</summary>
    LaunchPatient = 0,

    /// <summary>The app locates the patient itself after sign-in (e.g. <c>user/Patient.search</c> by MRN).</summary>
    AppDrivenSearch = 1,

    /// <summary>No patient context — user-level access only.</summary>
    None = 2
}
