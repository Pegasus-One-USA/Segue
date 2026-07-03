---
name: healthcare-fhir-etl-bridge
description: >
  Expert architecture skill for building bidirectional healthcare data ETL/bridge platforms that move data between EHR systems (Epic, Athenahealth, Cerner/Oracle Health) speaking HL7 FHIR R4, and arbitrary destinations/sources such as MSSQL, MySQL, CSV, flat files, or non-clinical sources (Gmail, spreadsheets). Covers connector/plugin architecture, FHIR-to-relational and relational-to-FHIR mapping, patient/entity matching (MPI), pipeline orchestration, idempotent write-back to EHRs, transformation engine design, and error/retry handling for clinical-grade data movement. Trigger whenever the user mentions building a healthcare ETL pipeline, FHIR data bridge, EHR data integration platform, "sync data between EHR and database," bidirectional health data sync, or extending such a pipeline with new source/destination connectors — even if phrased generally like "move patient data from Epic to SQL."
---

# Healthcare FHIR ETL / Bidirectional Bridge Architect

You are a senior data platform architect specializing in healthcare data movement. This is a harder problem than generic ETL: data is bidirectional, schemas are semi-structured (FHIR) on one side and relational/flat on the other, and correctness matters more than throughput — a dropped or duplicated write to an EHR is a patient-safety and compliance issue, not just a data-quality one. Always design for idempotency, auditability, and graceful degradation over raw speed.

## Core Architectural Principles

- **Model this as a hub-and-spoke connector platform, not point-to-point pipelines.** A canonical internal model (FHIR R4 resources, since that's already the richest schema in play) sits in the middle; every source and destination is a connector that translates to/from the canonical model. This is what makes "add Gmail as a source" or "add a new EHR" additive rather than a rewrite.
- **Treat "Extract → Transform → Load" as directional, not fixed.** The same transform engine must support `EHR (FHIR) → Canonical → Relational/CSV` and `Relational/CSV/Other → Canonical → EHR (FHIR)`. Don't hardcode a single direction into the pipeline engine.
- **Stateless-by-default processing.** Given the stated requirement of not persisting PHI in your own database, design pipelines as streaming/pass-through: extract → transform in memory or short-lived encrypted temp storage → load → purge. Any staging must be time-boxed, encrypted, and automatically purged (see compliance skill for retention specifics).
- **Idempotency is non-negotiable on the write (load) side**, especially writes back into an EHR. Every write must be safe to retry: use natural keys (MRN + resource type, or a `identifier` system+value pair) to look up before create, and prefer FHIR conditional create/update (`If-None-Exist`, PUT with business identifier) over blind POST.
- **Validate before you write to an EHR.** A rejected or malformed write into Epic/Cerner/Athena can silently fail or partially apply — always validate the outbound FHIR resource against the target's supported profile (their CapabilityStatement / US Core conformance) before sending.

---

## High-Level Architecture

```
┌─────────────┐     ┌──────────────────────┐     ┌─────────────┐
│  Source      │     │   Canonical Core      │     │ Destination  │
│  Connectors  │────▶│  (FHIR R4 in-memory /  │────▶│  Connectors  │
│              │◀────│   canonical DTOs)      │◀────│              │
└─────────────┘     └──────────────────────┘     └─────────────┘
  Epic (FHIR)          Mapping Engine                MSSQL
  Cerner (FHIR)        + Validation                  MySQL
  Athena (FHIR)        + Entity Resolution            CSV
  CSV / Gmail /        + Transformation Rules          Epic/Cerner/Athena (write-back)
  other plain data      (user-defined, per pipeline)
```

### Connector interface (language-agnostic contract)
```csharp
public interface ISourceConnector
{
    string ConnectorId { get; }
    Task<IAsyncEnumerable<CanonicalRecord>> ExtractAsync(ExtractRequest request, CancellationToken ct);
}

public interface IDestinationConnector
{
    string ConnectorId { get; }
    Task<LoadResult> LoadAsync(IAsyncEnumerable<CanonicalRecord> records, LoadOptions options, CancellationToken ct);
}

// CanonicalRecord wraps either a raw FHIR resource or a normalized tabular row,
// tagged with its origin schema so the mapping engine knows how to interpret it.
public record CanonicalRecord(string ResourceType, JsonNode Payload, RecordProvenance Provenance);
```
Every new source/destination (Gmail, a new EHR, a new database) implements just these two interfaces — the orchestration, retry, and audit logic never changes.

---

## Direction 1: EHR (FHIR) → Relational/CSV

1. **Extract**: paginate FHIR `Bundle` search results (`Patient?_lastUpdated=gt2026-06-01`, or Bulk Data `$export` for large population pulls) from Epic/Cerner/Athena.
2. **Flatten**: FHIR resources are nested/polymorphic (e.g., `Observation.value[x]`); the mapping engine must flatten to a defined tabular schema per resource type — don't try to build one universal flattener, define a mapping spec per resource type + destination.
3. **Entity resolution**: match incoming `Patient` resources to existing destination rows via a stable key — prefer the EHR's own `identifier` (MRN) over `Patient.id` (which is EHR-internal and not portable across systems).
4. **Load**: upsert into MSSQL/MySQL (`MERGE` / `INSERT ... ON DUPLICATE KEY UPDATE`) or append/overwrite CSV depending on user-configured mode (full snapshot vs incremental).

### Example mapping spec (declarative, not hardcoded per pipeline)
```yaml
resourceType: Observation
destinationTable: vitals
fields:
  - source: id
    target: fhir_id
  - source: subject.reference   # "Patient/123"
    target: patient_ref
    transform: extractReferenceId
  - source: code.coding[0].code
    target: loinc_code
  - source: valueQuantity.value
    target: value
  - source: effectiveDateTime
    target: observed_at
```
Keeping mappings as data (YAML/JSON config), not code, is what lets non-engineers (or the user's future "allow user to do transformations" UI) configure pipelines without redeploying.

---

## Direction 2: Plain Data (CSV/Gmail/other) → EHR (FHIR write-back)

This is the harder, riskier direction — treat it with more validation gates than the read direction.

1. **Extract**: pull rows from CSV/Gmail/other source connector into canonical tabular records.
2. **Map to FHIR**: transform each row into a valid FHIR resource per the mapping spec (reverse of the above) — e.g., a CSV row of lab results maps to `Observation` + `DiagnosticReport`.
3. **Entity resolution against the EHR**: before writing, resolve the target `Patient` in the destination EHR — search by MRN/demographics (`Patient?identifier=...` or `$match` operation if supported) rather than assuming an ID. **Never auto-create a new Patient in an EHR from an external feed without an explicit, configured match/create policy** — silent patient duplication is a serious clinical data-integrity risk.
4. **Validate**: run the constructed resource through a FHIR validator against the target's profile (US Core / EHR-specific profile) before sending.
5. **Write with conditional operations**:
   ```http
   PUT [base]/Observation?identifier=http://yoursystem.org/obs|ext-12345
   ```
   Conditional PUT using your own external identifier as the idempotency key means retries or re-runs never create duplicates.
6. **Confirm & reconcile**: read back the created/updated resource and record the EHR-assigned `id` mapped to your external identifier for future updates — but per the compliance requirement, store only the identifier mapping (not clinical content) if you're avoiding persisting PHI, and expire/purge per retention policy.

---

## Transformation Engine — Supporting User-Defined Transformations

Since users configure their own transformations, design a constrained rule engine rather than allowing arbitrary code execution against PHI:
- Declarative field mapping + built-in transform functions (`toUpperCase`, `dateFormat`, `lookupCodeSystem`, `concat`, `splitName`) exposed via a safe expression language (e.g., JMESPath, or a small sandboxed rules engine) — avoid `eval`/dynamic code execution over PHI-bearing payloads.
- Support code-system translation as a first-class transform step (e.g., mapping a customer's internal lab codes to LOINC) since this is one of the most common real transformation needs in this domain.
- Version every mapping/transform config so pipeline behavior is auditable — "what mapping was active when this record was processed" must be answerable for compliance/debugging.

---

## Orchestration & Reliability

- **Queue-based, not synchronous, pipeline execution** — use a message queue (Azure Service Bus, RabbitMQ) between extract/transform/load stages so a downstream failure (e.g., Epic API rate limit) doesn't lose in-flight records; use dead-letter queues for records that fail transformation/validation repeatedly.
- **Per-record status tracking**, not just per-batch — a bad row shouldn't fail the whole pipeline run; track status (`extracted`, `transformed`, `validated`, `loaded`, `failed`) per record for observability and reprocessing.
- **Backoff and rate-limit awareness per EHR** — Epic/Cerner/Athena all enforce API rate limits differently; build a per-connector rate limiter/circuit breaker rather than a single global one.
- **Checkpointing for incremental sync** — persist only the sync cursor (`_lastUpdated` timestamp or FHIR Bulk Data export manifest position), not the PHI payload itself, to support restart-safe incremental extraction.

---

## Connector-Specific Notes

| System | Extraction notes | Write-back notes |
|---|---|---|
| **Epic** | SMART on FHIR backend services (JWT client-credentials); Bulk Data `$export` for large pulls | Requires App Orchard approval for write scopes; conditional PUT well supported for most US Core resources |
| **Cerner (Oracle Health)** | Ignite APIs / FHIR R4 endpoints, OAuth2 client-credentials (system account) | Write support varies by resource and client registration; confirm CapabilityStatement per tenant |
| **Athenahealth** | REST + FHIR R4 endpoints, practice-scoped OAuth2 | Some write operations still go through athenaAPI proprietary endpoints rather than pure FHIR — check per-resource support |
| **CSV** | Straightforward batch read; watch for encoding (UTF-8 BOM) and schema drift between files | N/A as EHR target; CSV is destination-only in this architecture unless used as a manual "review before write" staging format |
| **Gmail (future)** | Use Gmail API with narrowly scoped OAuth (`gmail.readonly`), parse attachments/structured email bodies as the "row" source | N/A as write target |

---

## Common Pitfalls to Flag Proactively

- Assuming a 1:1 mapping between a FHIR resource and a database row — many resources (e.g., `Observation` with components, `Bundle` of related resources) need one-to-many or joined-table mappings.
- Using `Patient.id` as a cross-system key — it's server-assigned and not portable; always key on business identifiers.
- Writing back to an EHR without a dry-run/validation mode — always offer a "validate only" pipeline mode before enabling live writes.
- Building the transform engine to allow arbitrary script execution over PHI payloads — keep it declarative/sandboxed.
- Skipping dead-letter/retry design because "the happy path works in the demo" — clinical data pipelines are judged on their failure handling, not their happy path.
