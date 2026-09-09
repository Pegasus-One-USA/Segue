namespace HealthAppBackend;

// Landing tables for the Data Lake Webhook receiver (see DataLakeWebhookEndpoints). Three levels, because a
// lake consumer genuinely wants all three and FHIRBridge's own payload is shaped for it:
//
//   DataLake_Batch       one row per HTTP request  — the raw body exactly as received, plus the provenance
//                        headers FHIRBridge sends. This is the "store the entire JSON" half.
//   DataLake_Record      one row per record inside that request — NDJSON line, array element, or the single
//                        object of a one-request-per-record delivery — with that record's own raw JSON.
//   DataLake_RecordValue one row per mapped field on a record. This is the "store the properties" half, and
//                        it is key/value rather than fixed columns on purpose: the field names come from
//                        whatever the workflow's mapping profile happens to define, so a fixed-column table
//                        would need a schema change every time someone maps a different field.
//
// Guid keys are assigned in code rather than by IDENTITY: DatabaseSchemaReconciler creates these tables from
// the EF model on startup, and a client-assigned key needs nothing from it beyond a plain column.

/// <summary>One received HTTP request.</summary>
public sealed class DataLakeBatchEntity
{
    public Guid BatchId { get; set; }

    /// <summary>
    /// FHIRBridge's <c>X-Idempotency-Key</c>. Stable across its retries AND across a re-run of the same
    /// workflow, which is what makes the duplicate check in the endpoint meaningful rather than decorative.
    /// Null when something other than FHIRBridge posted here.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    public string? ResourceType { get; set; }
    public string? DestinationObject { get; set; }

    /// <summary>Records FHIRBridge said it was sending (<c>X-FHIRBridge-Record-Count</c>), for comparison
    /// against <see cref="RecordsStored"/> — a mismatch means this receiver failed to parse part of the body.</summary>
    public int? DeclaredRecordCount { get; set; }

    /// <summary>Which delivery attempt this was (<c>X-FHIRBridge-Attempt</c>) — greater than 1 proves a retry.</summary>
    public int? Attempt { get; set; }

    /// <summary>How the body was framed, as detected by the endpoint: Ndjson / JsonArray / Envelope / SingleObject.</summary>
    public string PayloadShape { get; set; } = string.Empty;

    public string? ContentType { get; set; }

    /// <summary>"gzip" when the request arrived compressed and was decompressed before parsing.</summary>
    public string? ContentEncoding { get; set; }

    /// <summary>Which auth scheme this receiver actually verified: None / Bearer / ApiKey / Hmac.</summary>
    public string AuthMode { get; set; } = string.Empty;

    /// <summary>The decompressed request body verbatim.</summary>
    public string RawBody { get; set; } = string.Empty;

    public int RawBodyBytes { get; set; }
    public int RecordsStored { get; set; }
    public DateTime ReceivedOnUtc { get; set; }
    public string? RemoteIpAddress { get; set; }
}

/// <summary>One record inside a batch.</summary>
public sealed class DataLakeRecordEntity
{
    public Guid RecordId { get; set; }
    public Guid BatchId { get; set; }

    /// <summary>Position within the batch, so NDJSON line order is recoverable.</summary>
    public int SequenceInBatch { get; set; }

    // The four provenance fields FHIRBridge puts on every record it emits. All nullable: an arbitrary JSON
    // document posted by hand (Postman, curl) carries none of them, and this receiver still stores it.
    public string? PipelineRunId { get; set; }
    public string? ResourceType { get; set; }
    public string? DestinationObject { get; set; }
    public string? SourceResourceId { get; set; }
    public DateTime? WrittenOnUtc { get; set; }

    /// <summary>This one record's JSON, verbatim.</summary>
    public string RawJson { get; set; } = string.Empty;

    public DateTime ReceivedOnUtc { get; set; }
}

/// <summary>One field of one record, flattened.</summary>
public sealed class DataLakeRecordValueEntity
{
    public Guid ValueId { get; set; }
    public Guid RecordId { get; set; }

    /// <summary>
    /// The mapped field name. Nested objects are flattened with a dotted path (e.g. <c>resource.id</c>) and
    /// arrays with an index (<c>name.0.family</c>), so an arbitrary hand-posted document flattens as
    /// predictably as a FHIRBridge record does.
    /// </summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary>Stringified value. Null stays null rather than becoming the text "null".</summary>
    public string? FieldValue { get; set; }

    /// <summary>The JSON kind the value came from: String / Number / True / False / Null.</summary>
    public string ValueKind { get; set; } = string.Empty;
}
