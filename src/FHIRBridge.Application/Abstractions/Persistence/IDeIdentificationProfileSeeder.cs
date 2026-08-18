namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>One-time: creates the "HIPAA Safe Harbor — Default" profile and its pre-mapping rules, ported
/// from the hardcoded rule list this feature replaced. Insert-only — never touches a profile an admin has
/// since created/edited, and no-ops on every startup after the first.</summary>
public interface IDeIdentificationProfileSeeder
{
    Task EnsureSeededAsync(CancellationToken cancellationToken);
}
