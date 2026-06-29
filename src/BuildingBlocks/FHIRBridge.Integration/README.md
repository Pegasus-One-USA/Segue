# FHIRBridge.Integration

> Healthcare interoperability primitives shared across stacks: FHIR JSON parsing/bundle building, a dependency-free HL7 v2 parser + MLLP + HL7-to-FHIR mapper, and SQL Server connection bootstrapping.

**Layer:** Building Block / cross-cutting · **SDK:** Microsoft.NET.Sdk · **Target:** net9.0

## Purpose
Integration holds the protocol-level interoperability helpers that both the configured pipeline and the synchronous runtime connectors need, so FHIR/HL7 handling lives in exactly one place. It parses and builds FHIR R4 JSON with pure `System.Text.Json` (no Firely dependency), parses HL7 v2 messages and frames/acknowledges them over MLLP, maps HL7 v2 (ADT/ORU/MDM) into FHIR bundles so legacy feeds flow through the same ingestion path as native FHIR, and provides SQL Server connection bootstrapping for destination writers.

## Responsibilities
- **FHIR JSON**: parse single resources, search bundles, and inbound webhook payloads into `ResourceEnvelope` value objects; extract the `next` pagination link; and build FHIR `searchset` bundles (including partial-failure `OperationOutcome` entries) — the single source of truth for FHIR JSON across stacks.
- **HL7 v2**: parse pipe-delimited v2 text into a navigable message model with HL7's 1-based field/component/repetition numbering; handle MLLP framing/extraction and AA/AE ACK construction; and map ADT/ORU/MDM messages into FHIR R4 bundles.
- **SQL**: open SQL Server / Azure SQL connections for destination writers, creating the target database if it does not exist, with safe identifier escaping.

## Key components
- **Fhir/**
  - `FhirResourceParser` — static parser producing `ResourceEnvelope`s. `ParseResource`, `ParseSearchBundle` (treats a no-results `OperationOutcome` as empty, throws on real errors, skips `search.mode = outcome` and `OperationOutcome` entries), `ParsePayload` (empty bundle is an error), and `GetNextLink`. Extracts `meta.versionId`/`lastUpdated`.
  - `FhirBundleBuilder` — builds a `searchset` Bundle from `ResourceEnvelope`s as `match` entries plus optional `ResourceFetchFailure` items rendered as `outcome` `OperationOutcome` entries, enabling best-effort aggregation that reports partial failure inline. Embeds each envelope's `RawJson` verbatim.
- **Hl7v2/**
  - `Hl7v2Parser` — parses raw v2 text into `Hl7v2Message`; reads delimiters from MSH-1/MSH-2 and special-cases header segments (MSH/BHS/FHS) so field positions line up with HL7 numbering.
  - `Hl7v2Message` / `Hl7Encoding` — navigable message: `Segment`/`SegmentsOf`, plus convenience accessors `MessageType`, `TriggerEvent`, `MessageControlId`.
  - `Hl7v2Segment` / `Hl7v2Field` — 1-based field access with component (`^`) and repetition (`~`) splitting.
  - `Hl7MllpProtocol` — MLLP framing constants and helpers: `Frame`, `TryExtractMessage`, `ContainsCompleteFrame`, and `BuildAck` (AA/AE with sender/receiver swap per HL7 convention).
  - `Hl7v2ToFhirMapper` — `MapToFhirBundleJson`: PID → Patient; ORU OBX → Observations (quantity vs string value, status mapping); MDM TXA + OBX → DocumentReference (base64 text attachment). Maps gender/date/status codes to FHIR equivalents.
- **Sql/**
  - `SqlServerConnectionFactory` — `OpenConnectionAsync`: ensures the target database exists (parameterized `DB_ID` check + `CREATE DATABASE` via `master`) then opens the connection. Shared by all SQL destination writers.
  - `SqlIdentifier` — bracket-quoted identifier escaping (`]` → `]]`) for safe DDL.

## Dependencies
- **Projects:** `FHIRBridge.Runtime.Domain` (for `ResourceEnvelope` and related value objects in `FHIRBridge.Runtime.Domain.ValueObjects`)
- **Key packages:** `Microsoft.Data.SqlClient`
- **Referenced by:** `FHIRBridge.Infrastructure`, `FHIRBridge.Runtime.Infrastructure`

## Current state in this skeleton
Only the `.csproj` (referencing `FHIRBridge.Runtime.Domain` and `Microsoft.Data.SqlClient`) exists in this skeleton. All source — `Fhir/FhirResourceParser.cs`, `Fhir/FhirBundleBuilder.cs`, the `Hl7v2/` set (`Hl7v2Parser`, `Hl7v2Message`, `Hl7v2Segment`, `Hl7MllpProtocol`, `Hl7v2ToFhirMapper`), and `Sql/` (`SqlServerConnectionFactory`, `SqlIdentifier`) — still needs porting from the reference. Note the build depends on `FHIRBridge.Runtime.Domain` existing, since the FHIR helpers consume its `ResourceEnvelope` value objects.

## Roadmap — what it will do in detail
Integration is where the platform's "speak every healthcare dialect, normalize to FHIR" promise is implemented, so it will expand outward from the current ADT/ORU/MDM + SQL baseline:
- **Port the reference implementation** so the runtime connectors and configured pipeline share one FHIR/HL7 codebase.
- **Broaden HL7 v2 coverage** — more trigger events and segment types (e.g. SIU scheduling, ORM orders, DFT financial, additional ADT events), repeating-group handling, and richer terminology mapping (LOINC/SNOMED) in `Hl7v2ToFhirMapper`.
- **FHIR robustness** — optional Firely-based validation/profiling behind an abstraction, transaction/batch bundle support, and `_include`/`_revinclude` handling for richer search responses.
- **Outbound HL7** — generate v2 messages from FHIR for systems that only consume v2, reusing the encoding model.
- **More destination protocols** — extend the `Sql` helpers and add connection bootstrapping for other relational/analytic destinations, keeping safe-identifier and database-provisioning logic centralized.
- **C-CDA / CDA support** — add a document module so clinical documents can also be normalized to FHIR through the same envelope abstraction.
