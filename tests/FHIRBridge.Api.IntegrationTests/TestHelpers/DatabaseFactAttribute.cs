using Xunit;

namespace FHIRBridge.Api.IntegrationTests.TestHelpers;

/// <summary>
/// A test that needs the PostgreSQL mode (FHIRBRIDGE_IT_DB; see run-against-postgres.ps1). Running a workflow
/// resolves services that need a database, so on the in-memory host the run endpoint fails before its own checks;
/// such a test is skipped there, with the reason shown, rather than passing for the wrong reason.
/// </summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (ApiFactory.DatabaseConnectionString is null)
        {
            Skip = "Needs a database (set FHIRBRIDGE_IT_DB or use run-against-postgres.ps1): running a workflow and the OAuth launch endpoints do.";
        }
    }
}
