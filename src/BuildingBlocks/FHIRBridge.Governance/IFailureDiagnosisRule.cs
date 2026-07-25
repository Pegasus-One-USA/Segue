namespace FHIRBridge.Governance;

/// <summary>
/// One known failure signature (e.g. "SQL login failed", "Epic token endpoint returned invalid_client"). Registered
/// in DI — one rule per signature a connector/writer already knows about — rather than grown as a single central
/// switch, mirroring the registry/strategy pattern this project already uses for EHR vendors and auth strategies.
/// </summary>
public interface IFailureDiagnosisRule
{
    bool Matches(Exception exception);

    Diagnosis Diagnose(Exception exception);
}
