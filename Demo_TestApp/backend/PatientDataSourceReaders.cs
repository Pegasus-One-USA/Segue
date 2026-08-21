using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;
using MySqlConnector;

namespace HealthAppBackend;

// Backs the BackendSystem role's "Data Source" radio group (SQL Server / MySQL / NoSQL) on the Patient List and
// Patient Details screens only — every other resource tab (Encounters, Observations, ...) still reads from
// HealthAppDb/SQL Server unconditionally, since only those two screens were asked to switch. Each reader targets
// the SAME conceptual "Patient_NewMapped" table/collection, just in a different store:
//   - Sql:   HealthAppDb.dbo.Patient_NewMapped (SQL Server) — the pre-existing path, unchanged.
//   - MySql: FHIRBridge's own output-mysql container, fhirbridge_output.Patient_NewMapped — the same database this
//            session proved out for FHIRBridge's MySQL destination writer.
//   - NoSql: FHIRBridge's own output-mongo container, fhirbridge_output.patients collection.
internal static class PatientDataSources
{
    public const string Sql = "sql";
    public const string MySql = "mysql";
    public const string NoSql = "nosql";

    /// <summary>Normalizes an untrusted query-string value to one of the three known sources, defaulting to Sql for
    /// anything missing/unrecognized rather than 400ing — a demo app's radio group should never be able to break
    /// the request just by an unexpected value slipping through.</summary>
    public static string Normalize(string? requested) => requested?.Trim().ToLowerInvariant() switch
    {
        MySql => MySql,
        NoSql => NoSql,
        _ => Sql,
    };
}

internal interface IPatientDataSourceReader
{
    Task<List<PatientListItemDto>> GetPatientsAsync(CancellationToken cancellationToken);

    Task<PatientDetailDto?> GetPatientAsync(string patientId, CancellationToken cancellationToken);
}

/// <summary>Resolves the reader for a normalized source key — one place that knows the string-to-implementation
/// mapping, so BackendSystemEndpoints.cs doesn't grow its own switch.</summary>
internal sealed class PatientDataSourceResolver
{
    private readonly IReadOnlyDictionary<string, IPatientDataSourceReader> _readers;

    public PatientDataSourceResolver(
        SqlPatientDataSourceReader sql,
        MySqlPatientDataSourceReader mySql,
        MongoPatientDataSourceReader noSql)
    {
        _readers = new Dictionary<string, IPatientDataSourceReader>
        {
            [PatientDataSources.Sql] = sql,
            [PatientDataSources.MySql] = mySql,
            [PatientDataSources.NoSql] = noSql,
        };
    }

    public IPatientDataSourceReader Resolve(string? requestedSource) =>
        _readers[PatientDataSources.Normalize(requestedSource)];
}

/// <summary>Unchanged behavior — same HealthAppDbContext/SQL Server query BackendSystemEndpoints.cs always ran,
/// just moved behind the shared reader interface so it's one of three interchangeable options instead of the
/// only option.</summary>
internal sealed class SqlPatientDataSourceReader : IPatientDataSourceReader
{
    private readonly HealthAppDbContext _db;

    public SqlPatientDataSourceReader(HealthAppDbContext db)
    {
        _db = db;
    }

    public async Task<List<PatientListItemDto>> GetPatientsAsync(CancellationToken cancellationToken)
    {
        return await _db.BackendSystemPatients
            .OrderBy(p => p.FamilyName)
            .ThenBy(p => p.GivenName)
            .Select(p => new PatientListItemDto(
                p.PatientId,
                BackendSystemEndpoints.BuildFullNamePublic(p.GivenName, p.MiddleName, p.FamilyName),
                p.MRN,
                p.Identifier,
                p.Gender,
                p.BirthDate))
            .ToListAsync(cancellationToken);
    }

    public async Task<PatientDetailDto?> GetPatientAsync(string patientId, CancellationToken cancellationToken)
    {
        var patient = await _db.BackendSystemPatients
            .FirstOrDefaultAsync(p => p.PatientId == patientId, cancellationToken);
        return patient is null ? null : ToDetailDto(patient);
    }

    private static PatientDetailDto ToDetailDto(PatientNewMappedEntity patient) => new(
        patient.PatientId,
        patient.Identifier,
        patient.MRN,
        patient.FamilyName,
        patient.GivenName,
        patient.MiddleName,
        BackendSystemEndpoints.BuildFullNamePublic(patient.GivenName, patient.MiddleName, patient.FamilyName),
        patient.Gender,
        patient.BirthDate,
        patient.Deceased,
        patient.MaritalStatus,
        patient.Phone,
        patient.Email,
        patient.AddressLine1,
        patient.AddressLine2,
        patient.City,
        patient.State,
        patient.PostalCode,
        patient.Country);
}

/// <summary>Reads the same Patient_NewMapped shape from FHIRBridge's output-mysql container (fhirbridge_output
/// database). Plain ADO.NET via MySqlConnector rather than a second EF Core provider/DbContext — only two simple
/// queries are needed here, so a full second context (and its own design-time factory) would be pure overhead.</summary>
internal sealed class MySqlPatientDataSourceReader : IPatientDataSourceReader
{
    private const string SelectListSql = """
        SELECT PatientId, Identifier, MRN, FamilyName, GivenName, MiddleName, Gender, BirthDate
        FROM Patient_NewMapped
        ORDER BY FamilyName, GivenName
        """;

    private const string SelectByIdSql = """
        SELECT PatientId, Identifier, MRN, FamilyName, GivenName, MiddleName, Gender, BirthDate, Deceased,
               MaritalStatus, Phone, Email, AddressLine1, AddressLine2, City, State, PostalCode, Country
        FROM Patient_NewMapped
        WHERE PatientId = @patientId
        """;

    private readonly string _connectionString;

    public MySqlPatientDataSourceReader(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("MySqlOutput")
            ?? "Server=localhost;Port=3306;Database=fhirbridge_output;Uid=fhirbridge;Pwd=fhirbridge;";
    }

    public async Task<List<PatientListItemDto>> GetPatientsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(SelectListSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<PatientListItemDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var givenName = ReadString(reader, "GivenName");
            var middleName = ReadString(reader, "MiddleName");
            var familyName = ReadString(reader, "FamilyName");
            results.Add(new PatientListItemDto(
                reader.GetString(reader.GetOrdinal("PatientId")),
                BackendSystemEndpoints.BuildFullNamePublic(givenName, middleName, familyName),
                ReadString(reader, "MRN"),
                ReadString(reader, "Identifier"),
                ReadString(reader, "Gender"),
                ReadDateOnly(reader, "BirthDate")));
        }

        return results;
    }

    public async Task<PatientDetailDto?> GetPatientAsync(string patientId, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(SelectByIdSql, connection);
        command.Parameters.AddWithValue("@patientId", patientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var givenName = ReadString(reader, "GivenName");
        var middleName = ReadString(reader, "MiddleName");
        var familyName = ReadString(reader, "FamilyName");

        return new PatientDetailDto(
            reader.GetString(reader.GetOrdinal("PatientId")),
            ReadString(reader, "Identifier"),
            ReadString(reader, "MRN"),
            familyName,
            givenName,
            middleName,
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
            ReadString(reader, "Country"));
    }

    private static string? ReadString(MySqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static bool? ReadBool(MySqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);
    }

    private static DateOnly? ReadDateOnly(MySqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : DateOnly.FromDateTime(reader.GetDateTime(ordinal));
    }
}

/// <summary>Reads the same Patient_NewMapped shape from FHIRBridge's output-mongo container (fhirbridge_output
/// database, "patients" collection) — one document per patient, field names matching the SQL/MySQL columns
/// exactly so the same seed data (see docker-compose's output-mongo service) reads identically everywhere.</summary>
internal sealed class MongoPatientDataSourceReader : IPatientDataSourceReader
{
    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoPatientDataSourceReader(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("MongoOutput")
            ?? "mongodb://fhirbridge:fhirbridge@localhost:27017";
        var databaseName = configuration["Mongo:Database"] ?? "fhirbridge_output";
        var collectionName = configuration["Mongo:PatientsCollection"] ?? "patients";

        var client = new MongoClient(connectionString);
        _collection = client.GetDatabase(databaseName).GetCollection<BsonDocument>(collectionName);
    }

    public async Task<List<PatientListItemDto>> GetPatientsAsync(CancellationToken cancellationToken)
    {
        var documents = await _collection.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("FamilyName").Ascending("GivenName"))
            .ToListAsync(cancellationToken);

        return documents.Select(doc => new PatientListItemDto(
            GetString(doc, "PatientId") ?? string.Empty,
            BackendSystemEndpoints.BuildFullNamePublic(GetString(doc, "GivenName"), GetString(doc, "MiddleName"), GetString(doc, "FamilyName")),
            GetString(doc, "MRN"),
            GetString(doc, "Identifier"),
            GetString(doc, "Gender"),
            GetDateOnly(doc, "BirthDate"))).ToList();
    }

    public async Task<PatientDetailDto?> GetPatientAsync(string patientId, CancellationToken cancellationToken)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("PatientId", patientId);
        var doc = await _collection.Find(filter).FirstOrDefaultAsync(cancellationToken);
        if (doc is null)
        {
            return null;
        }

        var givenName = GetString(doc, "GivenName");
        var middleName = GetString(doc, "MiddleName");
        var familyName = GetString(doc, "FamilyName");

        return new PatientDetailDto(
            GetString(doc, "PatientId") ?? patientId,
            GetString(doc, "Identifier"),
            GetString(doc, "MRN"),
            familyName,
            givenName,
            middleName,
            BackendSystemEndpoints.BuildFullNamePublic(givenName, middleName, familyName),
            GetString(doc, "Gender"),
            GetDateOnly(doc, "BirthDate"),
            GetBool(doc, "Deceased"),
            GetString(doc, "MaritalStatus"),
            GetString(doc, "Phone"),
            GetString(doc, "Email"),
            GetString(doc, "AddressLine1"),
            GetString(doc, "AddressLine2"),
            GetString(doc, "City"),
            GetString(doc, "State"),
            GetString(doc, "PostalCode"),
            GetString(doc, "Country"));
    }

    private static string? GetString(BsonDocument doc, string field) =>
        doc.TryGetValue(field, out var value) && !value.IsBsonNull ? value.AsString : null;

    private static bool? GetBool(BsonDocument doc, string field) =>
        doc.TryGetValue(field, out var value) && !value.IsBsonNull ? value.AsBoolean : null;

    private static DateOnly? GetDateOnly(BsonDocument doc, string field)
    {
        if (!doc.TryGetValue(field, out var value) || value.IsBsonNull)
        {
            return null;
        }

        return DateOnly.TryParse(value.AsString, out var parsed) ? parsed : null;
    }
}
