using Microsoft.Data.SqlClient;

namespace HealthAppBackend;

// GET /api/pipeline-runs/{runId}/patients — every Patient_NewMapped row a given FHIRBridge pipeline run wrote.
//
// PipelineRunId is one of the lineage columns FHIRBridge's SQL Server destination writer stamps on each row it
// writes (alongside ResourceType/SourceResourceId/WrittenOnUtc — see MappedSqlServerDestinationWriter's
// InsertSystemColumns), so it is the natural handle for "show me what this run landed". It is NOT on
// PatientNewMappedEntity: that entity models only the mapped clinical columns the rest of this app reads, and
// adding a lineage column to it would change what every existing BackendSystem query projects. Hence plain
// ADO.NET here, in the same spirit as MySqlPatientDataSourceReader — two narrow queries don't justify reshaping
// the shared entity.
public static class PipelineRunPatientEndpoints
{

    // Only columns confirmed to exist on the table are selected; a SELECT * would bind this endpoint to whatever
    // extra mapped columns a given workflow's mapping profile happened to add.
    private const string SelectByRunIdSql = """
        SELECT PatientId, Identifier, MRN, FamilyName, GivenName, MiddleName, Gender, BirthDate, Deceased,
               MaritalStatus, Phone, Email, AddressLine1, AddressLine2, City, State, PostalCode, Country,
               PipelineRunId
        FROM dbo.Patient_NewMapped
        WHERE PipelineRunId = @pipelineRunId
        ORDER BY FamilyName, GivenName
        """;

    public static void MapPipelineRunPatientEndpoints(this WebApplication app)
    {
        // Anonymous by design (no session-cookie check), for the same reason /api/provider-in-app-launch-context
        // and /api/account-context-link/check already are: the only caller is launch-provider-in-app.ts on the
        // post-OAuth return leg, which the browser reaches by a CROSS-SITE redirect from FHIRBridge (itself
        // redirected from Epic). hb_session is SameSite=Lax, so it is NOT attached on that navigation's
        // subsequent XHRs from an embedded ("Embedded" launch display mode) iframe — a session check here 401s
        // on every real EHR launch, which is exactly the failure this replaced.
        //
        // What guards the data instead: the run id is an unguessable server-minted GUID that FHIRBridge only ever
        // hands to the app that originated the launch (via the validated callerId redirect — see
        // OAuthController.LaunchPipeline's allowed-origins check), so holding one is itself the proof of having
        // completed that launch. Same trust model FHIRBridge's own /latest-launch-result and
        // /workflows/checkpoint/{token} endpoints use. Note this is a DEMO app over dummy data; a production
        // equivalent should bind the run id to the requesting account server-side rather than rely on
        // unguessability alone (FHIRBridge's own launch-result endpoint has the same gap — see the
        // history.replaceState comment in launch-provider-in-app.ts's ngOnInit).
        app.MapGet("/api/pipeline-runs/{runId}/patients", async (
            string runId,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            // A malformed run id is a client error, not an empty result — returning 200/[] would make a typo'd
            // GUID indistinguishable from a run that genuinely wrote no patients.
            if (!Guid.TryParse(runId, out var pipelineRunId))
            {
                return Results.BadRequest(new { error = $"'{runId}' is not a valid pipeline run id (expected a GUID)." });
            }

            var connectionString = configuration.GetConnectionString("Default")
                ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

            var patients = new List<PipelineRunPatientDto>();

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(SelectByRunIdSql, connection);
            command.Parameters.Add("@pipelineRunId", System.Data.SqlDbType.UniqueIdentifier).Value = pipelineRunId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var givenName = ReadString(reader, "GivenName");
                var middleName = ReadString(reader, "MiddleName");
                var familyName = ReadString(reader, "FamilyName");

                patients.Add(new PipelineRunPatientDto(
                    ReadString(reader, "PatientId") ?? string.Empty,
                    ReadString(reader, "Identifier"),
                    ReadString(reader, "MRN"),
                    familyName,
                    givenName,
                    middleName,
                    // Reuses the BackendSystem name-joining rule so a patient reads identically whether it came
                    // from here or from /api/backend-system/patients.
                    BackendSystemEndpoints.BuildFullNamePublic(givenName, middleName, familyName),
                    ReadString(reader, "Gender"),
                    ReadDateOnly(reader, "BirthDate"),
                    ReadBool(reader, "Deceased"),
                    ReadString(reader, "MaritalStatus"),
                    ReadString(reader, "Phone"),
                    ReadString(reader, "Email"),
                    ReadString(reader, "AddressLine1"),
                    ReadString(reader, "AddressLine2"),
                    ReadString(reader, "City"),
                    ReadString(reader, "State"),
                    ReadString(reader, "PostalCode"),
                    ReadString(reader, "Country"),
                    ReadGuid(reader, "PipelineRunId")));
            }

            // 200 with an empty list, not 404: the run id is well-formed and may simply have written no Patient
            // rows (a workflow can target other resource types), which is a valid answer rather than "not found".
            return Results.Ok(new PipelineRunPatientsResponse(pipelineRunId, patients.Count, patients));
        });
    }

    private static string? ReadString(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static bool? ReadBool(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);
    }

    private static DateOnly? ReadDateOnly(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : DateOnly.FromDateTime(reader.GetDateTime(ordinal));
    }

    private static Guid? ReadGuid(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }
}

record PipelineRunPatientsResponse(
    Guid PipelineRunId,
    int Count,
    IReadOnlyList<PipelineRunPatientDto> Patients);

// Same fields as PatientDetailDto plus the run id the row was written by — a separate record rather than a reuse
// so adding lineage here can't alter what the BackendSystem patient-detail screen receives.
record PipelineRunPatientDto(
    string PatientId,
    string? Identifier,
    string? MRN,
    string? FamilyName,
    string? GivenName,
    string? MiddleName,
    string? FullName,
    string? Gender,
    DateOnly? BirthDate,
    bool? Deceased,
    string? MaritalStatus,
    string? Phone,
    string? Email,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    string? Country,
    Guid? PipelineRunId);
