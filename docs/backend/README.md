# Segue Backend — Architecture Guide

> **Audience:** developers working on the Segue backend.
> **Scope:** the .NET solution (`src/`, `tests/`). The Angular `portal/` is out of scope here.
> **Last reviewed:** 2026-07-01 (branch `port/stepbase-overlay`). Please keep these docs in sync when backend code changes.

## What Segue is (plain English)

Segue is a **multi-tenant healthcare data-integration platform** (HIPAA-oriented). It's a "universal adapter + pipeline" between EHR systems and wherever an organization needs their clinical data to land.

End to end:
1. **Connect** to a source EHR (Epic, Healow, MEDITECH Greenfield, or any FHIR / HL7 v2 system) — handling the messy auth (SMART-on-FHIR, OAuth, backend-service JWTs).
2. **Pull** clinical records (Patient, Observation, …) via FHIR search / HL7 v2 feeds.
3. **Clean & govern** — normalize formats, validate against US-Core, score data quality, log access (PHI-free), optionally de-identify.
4. **Map** each record's fields to a target shape.
5. **Write** to a destination — SQL, data warehouses (Snowflake/Databricks), BI tools (Power BI/Tableau), files (CSV/Parquet/…), cloud storage (S3/Blob), or another FHIR server.

Admins configure this **per tenant** (sources → mappings → destinations → routes) via the REST API; pipelines run **on demand, on a schedule, or via webhook / FHIR Subscription push**. HIPAA posture throughout: append-only tamper-evident audit logs, PHI masking in logs, soft-deletes, retention/purge.

## Solution shape

~17 projects, **Clean Architecture / DDD**, layered `Domain ← Application ← Infrastructure ← Api` (enforced by architecture tests). See [`../../STRUCTURE.md`](../../STRUCTURE.md) for the full project + dependency + NuGet table.

- **BuildingBlocks** — `SharedKernel`, `Integration`, `Observability` are **built**; `Contracts`, `EventBus` (MassTransit), `Messaging` (Azure Service Bus) are **package-only stubs**.
- **Core app** — `FHIRBridge.Domain`, `FHIRBridge.Application`, `FHIRBridge.Infrastructure`.
- **Runtime plane** — `Runtime.Domain` / `Runtime.Application` / `Runtime.Infrastructure`: the pipeline engine + ranked-node workflow engine.
- **ControlPlane** — `ControlPlane.Domain` / `ControlPlane.Application`: **empty scaffolds** (future split of the configuration/authoring plane from the runtime/execution plane).
- **Hosts** — `Api` (REST, `/api/v1`), `Worker` (background jobs), `Gateway` (**YARP reverse-proxy stub**, no routes yet).

> ⚠️ `STRUCTURE.md` was written at "skeleton" time and describes the copied scaffold; the concrete code described in these docs has since been filled in.

## Two pipeline execution paths (important)

They coexist — know which one you're touching:

1. **Configured pipeline** — `IConfiguredPipelineService` (core Application/Infrastructure). Driven by tenant config; used by the API's `PipelineRunsController` and the `Worker`. Owns the rich stack: **21 destination writers**, terminology, 4-step normalization, governance/de-id/retention.
2. **Runtime plane** — `PipelineOrchestrator` (explicit DAG, fan-out/fan-in, 2 writers) + the **ranked-node workflow engine** exposed via the API workflow endpoints.

## Design conventions

- **Registry/strategy over `switch`.** Architecture tests forbid `switch` on `ApplicationType` / `RuntimeSourceType`. New EHR vendor, auth flow, or output format = a new class + one DI registration, not edits to existing code.
- **Two auth axes:** vendor = inheritance; application-type (Backend / EHR-Launch / Standalone / Patient) = composition via a strategy registry.
- **Runtime mode:** SQL Server when a connection string is set, else in-memory repositories/stores (dev). Redis cache when configured, else memory.

## Contents

| Doc | Covers |
|---|---|
| [01 — Domain & Data](01-domain-and-data.md) | Tenant aggregate + children, RBAC tables, audit/lineage, the 18-DbSet EF context, conventions, migrations |
| [02 — API Surface](02-api-surface.md) | ~15 v1 controllers + workflow endpoints, dual-scheme JWT auth, authorization policies, middleware order |
| [03 — Runtime Pipeline](03-runtime-pipeline.md) | DAG orchestrator, source connectors, SMART token providers + ApplicationType strategies, destinations, workflow engine, Worker |
| [04 — App Services & Building Blocks](04-app-services-and-building-blocks.md) | Application services, rich Infrastructure capabilities, built-vs-stub building blocks, test suite |
| [05 — Workflow Node Checkpoints Plan](05-workflow-node-checkpoints-plan.md) | **Proposed, not yet built.** Per-node "Copy URL" / partial-execution checkpoints for the ranked workflow engine — implement this doc verbatim when picked up |
| [06 — Permission Auto-Generation](05-permission-auto-generation.md) | How a permission's Id/Name/DisplayName is generated, the two sync pipelines (seed-declared vs. discovered), boot-time flow, file-by-file changelog, multi-declaration behavior |
| [07 — Adding Permissions (How-To)](06-adding-permissions-howto.md) | Practical steps: adding a Category/Group/Action, applying `[StandardPermission]`, reusing a permission across endpoints, seed-declared vs. discovered-only |
| [08 — Governance Logging Status](08-governance-logging-status.md) | Phase-by-phase completion status of the audit/governance logging rebuild — what's done, what's dormant, what's not started, and what to pick up next |

## Maturity flags (as of this review)

- **Planned, not started:** per-node checkpoint URLs / partial DAG execution — see [05](05-workflow-node-checkpoints-plan.md).
- **Stubs / not implemented:** `Contracts`, `EventBus`, `Messaging` building blocks; both `ControlPlane` projects; the `Gateway` (YARP) has no routes.
- **Phase-1 no-ops:** the Runtime plane's transform step (`FhirResourceNormalizer` only compacts JSON) and some governance placeholders in the *core Application* layer — the real normalization/governance/de-id implementations live in `FHIRBridge.Infrastructure`.
- **Governance/audit logging (rebuilt 2026-07):** compliance-critical parts (audit trail, local auth, SSO, SMART launch, data-access logging) are done and tested; several Operations tables (`SchedulerHistory`, `RetryHistory`) are likely dormant because the Worker host doesn't register the queue-processing hosted services they depend on. Data Lineage, Queue Monitor, Alert Engine, OTLP export, and retention enforcement for the new tables are not started. See [08](08-governance-logging-status.md) for the full phase-by-phase breakdown.
