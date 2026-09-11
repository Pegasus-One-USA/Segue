# FHIRBridge.Observability

> Centralized OpenTelemetry + Serilog wiring and the in-process pipeline-metrics source that powers the admin dashboard, with PHI masking built in.

**Layer:** Building Block / cross-cutting · **SDK:** Microsoft.NET.Sdk · **Target:** net9.0

## Purpose
Observability is the single place where every FHIRBridge host (API, Worker, ControlPlane, Runtime, Gateway) turns on telemetry. It implements the PHI-free metrics contracts declared in `FHIRBridge.SharedKernel`, wires the OpenTelemetry tracing/metrics pipeline (ASP.NET Core, HTTP, .NET runtime, and the custom FHIRBridge meter) with optional OTLP and Azure Monitor exporters, and configures a shared Serilog logger with correlation enrichment and PHI masking. The custom metrics source doubles as an in-process accumulator so operators get throughput/latency on the admin dashboard without needing a separate metrics backend.

## Responsibilities
- Implement `IPipelineMetrics` (record completed runs) and `IMetricsSnapshotProvider` (point-in-time dashboard view) from SharedKernel in a single thread-safe singleton.
- Publish pipeline counters/histograms through a .NET `Meter` (`FHIRBridge.Pipeline`) for OpenTelemetry scraping, while maintaining a parallel bounded in-memory window of recent runs.
- Provide a one-call DI extension (`AddFhirBridgeObservability`) that registers the metrics source and, unless disabled, the full OpenTelemetry tracing + metrics pipeline with OTLP / Azure Monitor exporters.
- Provide a one-call Serilog bootstrap (`ConfigureFhirBridge`) that wires console + optional Seq (dev) and Application Insights (prod) sinks, enables `LogContext` correlation enrichment, and applies a **PHI-masking enricher** so patient identifiers never reach a sink.
- Own the `Observability` configuration section binding (`ObservabilityOptions`).

## Key components
- `FhirBridgeMetrics` — the custom metrics source. Implements `IPipelineMetrics` + `IMetricsSnapshotProvider` + `IDisposable`. Publishes six instruments (`fhirbridge.pipeline.runs`, `resources_extracted`, `records_mapped`, `records_written`, `errors`, and a `run_duration` histogram) tagged by status, and accumulates lifetime totals plus a bounded `LinkedList` of recent runs. `GetSnapshot()` computes average/P95/max duration and an estimated records-per-hour throughput over the recent window.
- `ObservabilityServiceCollectionExtensions.AddFhirBridgeObservability(services, configuration, serviceName)` — registers the singleton metrics source (as itself, `IPipelineMetrics`, and `IMetricsSnapshotProvider`), then conditionally adds OpenTelemetry tracing (ASP.NET Core + HTTP client) and metrics (ASP.NET Core + HTTP + runtime + custom meter) with OTLP and Azure Monitor exporters when endpoints/connection strings are configured.
- `ObservabilityOptions` — binds the `Observability` section: `Enabled`, `OtlpEndpoint`, `AzureMonitorConnectionString`, `EnableTracing`, `EnableMetrics`, `RecentRunBufferSize`.
- **Logging/**
  - `FhirBridgeLogging.ConfigureFhirBridge(...)` — shared Serilog configuration: console + optional Seq + Application Insights sinks, framework-noise minimum-level overrides, `FromLogContext` correlation, and the PHI-masking enricher; still honors a `Serilog` config section via `ReadFrom.Configuration`.
  - `PhiMaskingEnricher` — masks structured log properties whose name matches a PHI-sensitive set (ssn, mrn, birthDate, name, address, telecom, etc.); the masked set is overridable via `Observability:Phi:MaskedProperties`.

## Dependencies
- **Projects:** `FHIRBridge.SharedKernel`
- **Key packages:** OpenTelemetry (`Extensions.Hosting`, `Exporter.OpenTelemetryProtocol`, `Instrumentation.AspNetCore`, `Instrumentation.Http`, `Instrumentation.Runtime`), `Azure.Monitor.OpenTelemetry.Exporter`, Serilog (`Settings.Configuration`, `Sinks.Console`, `Sinks.Seq`, `Sinks.ApplicationInsights`), `Microsoft.Extensions.Configuration.Binder`, `Microsoft.Extensions.Options.ConfigurationExtensions`
- **Referenced by:** `FHIRBridge.Api`, `FHIRBridge.Worker`, `FHIRBridge.Gateway`, `FHIRBridge.UnitTests`

## Current state in this skeleton
Only the `.csproj` (with package + SharedKernel references) exists in this skeleton. The implementation files — `FhirBridgeMetrics.cs`, `ObservabilityOptions.cs`, `ObservabilityServiceCollectionExtensions.cs`, and `Logging/FhirBridgeLogging.cs` — still need to be ported from the reference implementation. Until then, hosts cannot call `AddFhirBridgeObservability` or `ConfigureFhirBridge`, and no `IPipelineMetrics`/`IMetricsSnapshotProvider` implementation is registered.

## Roadmap — what it will do in detail
This block is the observability backbone for a HIPAA-sensitive platform, so the roadmap prioritizes correctness, privacy, and operability:
- **Port the reference implementation** so every host gets metrics, traces, and structured logging from a single `AddFhirBridgeObservability` + `ConfigureFhirBridge` pair.
- ~~**Trace context propagation** across the async pipeline (API → Service Bus → Worker → Runtime) so a single pipeline run is one distributed trace, with `PipelineRunId`/`CorrelationId` carried via `LogContext` and baggage.~~
  **Delivered (partial):** `CorrelationId` is now tagged onto `Activity.Current` at the two seams that establish it (the Api correlation middleware, `AmbientActorContextExtensions.BeginCorrelatedScope`), and `Enrich.WithSpan()` puts `TraceId`/`SpanId` on every Serilog line. Cross-process continuity is still best-effort/`Azure.*`-source-only — RabbitMQ and the in-memory `IMessageConsumer` transports don't propagate W3C trace headers across the queue hop yet; that remains a follow-up.
- ~~**Custom activity sources** for pipeline stages (extract / map / write) to complement the counters, giving span-level latency attribution.~~
  **Delivered:** `FhirBridgeActivitySource` (`FHIRBridge.Pipeline`), registered via `.AddSource(...)` in `AddFhirBridgeObservability`, wraps `PipelineRunCommandHandler`, `WebhookIngestionCommandHandler`, and `PipelineOrchestrator.StartAsync`/`StartBulkExportAsync` in a `PipelineRun.Process` span tagged with `correlation_id`.
- **Health and readiness checks** surfaced alongside metrics, and a hardened admin-dashboard snapshot endpoint backed by `IMetricsSnapshotProvider`.
- **Alerting hooks** — error-rate and failed-run thresholds exported to Azure Monitor / OTLP backends.
- **Hardened PHI masking** — expand the masked-property set, add value-pattern masking (e.g. detect MRN/SSN shapes), and verify masking against log fixtures so PHI can never reach a sink, satisfying audit requirements.
- **Per-source metric dimensions** as the dashboard grows, kept aligned with the PHI-free contracts in SharedKernel.

---

## Structured logging: lifecycle coverage and Seq

### Enabling the Seq sink

`FhirBridgeLogging.ConfigureFhirBridge` adds the Seq sink **only when `Observability:SeqServerUrl` is non-empty**. The
base `appsettings.json` of the API and Worker now declares the key with an empty value, so the knob is discoverable —
but Seq stays off until an environment supplies a URL. `appsettings.Development.json` supplies
`http://localhost:5341` (the Docker Compose Seq), so local runs are wired out of the box.

Deployed hosts do **not** load `appsettings.Development.json` — the deploy script sets no `DOTNET_ENVIRONMENT`, so
services run as Production. To turn Seq on for a deployed environment, set either:

- the service environment variable `Observability__SeqServerUrl=http://<seq-host>:5341`, or
- `Observability:SeqServerUrl` in the hand-provisioned config overlaid from
  `C:\inetpub\FHIRBridge_Configurations\<Environment>\fhirbridge-worker\` (and `...\fhirbridge-api\`).

With Seq off, the same events still reach the rolling file sink (`Observability:LogFilePath`) and — for failures —
the Global Exception Manager and `WorkflowRun` tables that back the portal's Errors screen.

### Event catalog

`Logging/LogEvents.cs` assigns a stable `EventId` per lifecycle event. Seq's own `@i` event-type hash is derived from
the message template and changes whenever wording does, so dashboards should filter on `EventId.Id` instead. Ranges
group by stage, which makes whole-stage filters possible:

| Range | Stage | Wired in |
|---|---|---|
| 1xxx | Configuration / first-time setup | `ConfigurationService`, `SqlWorkflowDefinitionStore`, `SourceConnectionTestService`, API `BootstrapDatabase` |
| 2xxx | Scheduling, queue dispatch/consume, time zone | `Worker`, `ScheduleEvaluationService`, `ScheduleDispatcher`, `PipelineRunCommandProcessor`, `WebhookIngestionCommandProcessor`, `InProcessSystemSettingsCache` |
| 3xxx | Workflow run + node lifecycle | `RankedWorkflowOrchestrator` |
| 4xxx | Source / EHR (auth, extraction, cursors) | `CompositeFhirAccessTokenProvider`, `SourceNodeExecutor` |
| 5xxx | Governance / transform | `DeIdentificationNodeExecutor`, `MappingNodeExecutor`, `FhirResourceTransformNodeExecutor`, `TerminologyNodeExecutor` |
| 6xxx | Destination writes | `LoggingConfiguredDestinationWriter` |
| 7xxx | Authentication, session, user administration | `LocalAuthService`, `SsoAuthService`, `UserManagementService`, `SetupService` |

Node executors reach their logger through `WorkflowNodeExecutorBase.Logger`, named for the concrete executor.
Use it only for detail the orchestrator cannot see from outside — it already records node start, completion,
duration and record count generically.

Both execution paths are covered symmetrically. The configured pipeline (`ConfiguredPipelineService`) logs a
per-route scope plus an `extracted → mapped → written` line per route and a run-level terminal summary, mirroring
the Runtime plane's per-node and per-run events. `PipelineRunCommandProcessor` — the **default** route-scheduling
path, since `RuntimeWorkerOptions.DirectRouteSchedulingEnabled` is off — logs consume/complete/fail per message.

Two decorator/base-class choke points keep the rest from being per-implementation work:

- **`LoggingConfiguredDestinationWriter`**, applied in `ConfiguredDestinationWriterFactory.Create`, gives all ~26
  destination types identical write telemetry — and instruments any destination added later automatically.
- **`SourceNodeExecutor`** (the base every vendor source executor derives from) covers all EHR vendors at once. Its
  logger is named for the concrete subclass, so `SourceContext` in Seq identifies the vendor.

Because both the Runtime plane and the legacy configured-pipeline path resolve destinations through that factory and
tokens through `CompositeFhirAccessTokenProvider`, both execution paths inherit destination and auth telemetry.

### Correlation

`RankedWorkflowOrchestrator` opens an `ILogger.BeginScope` around each node execution carrying `WorkflowRunId`,
`WorkflowId`, `NodeId`, `NodeType`, `NodeRank` and `CorrelationId`. Everything logged beneath it — connector,
governance, destination writer — inherits those properties, so `WorkflowRunId = '<guid>'` in Seq returns a complete
run without each component re-logging the ids. `CorrelationId` is also pushed by
`IAmbientActorContext.BeginCorrelatedScope` and stored on the `WorkflowRun` row, so the portal's Errors screen and
Seq can be pivoted between.

### Useful Seq queries

```
-- Did my scheduled workflow fire, and was its time zone honoured?
EventId.Id = 2012                      -- WorkflowScheduleDue
TimeZoneResolved = false               -- silent UTC fallback; the schedule fires at the wrong local time

-- One run, end to end (every stage, every vendor, every destination)
WorkflowRunId = '<guid>'

-- Per-stage timings for a run
EventId.Id in [3101, 3102] and WorkflowRunId = '<guid>'

-- Extraction volume and incremental behaviour
EventId.Id = 4031                      -- ResourceTypeExtracted (RecordCount, IncrementalWatermark per type)
EventId.Id = 4042                      -- IncrementalWatermarkAdvanced (next run's _lastUpdated floor)
EventId.Id = 4032                      -- ResourceTypeSkipped (what a PartialSuccess actually dropped)

-- Auth, the most common first-time-setup failure
EventId.Id in [4001, 4002]
EventId.Id = 4002                      -- failures only

-- Destination writes, all types
EventId.Id >= 6000 and EventId.Id < 7000
EventId.Id = 6002 and RecordErrorCount > 0   -- partially-written batches (no exception thrown)

-- Governance and transform stage detail
EventId.Id = 5001                      -- GovernanceApplied (de-identification: record count + profile id)
EventId.Id = 5011                      -- TransformCompleted (mapping / transform / terminology outcomes)
EventId.Id = 5011 and RuleErrorCount > 0     -- failed rule hops (degrade a field, never fail the run)

-- Configured-pipeline (route) runs, the non-Runtime path
PipelineRunId = '<guid>'               -- one run, every route
RouteId = '<guid>'                     -- one route across runs

-- Queue dispatch → consume, across the transport
MessageId = '<id>'                     -- enqueue, consume, complete/fail for one command incl. retries
EventId.Id in [2031, 2032, 2033, 2034] -- processor started / consumed / completed / failed
EventId.Id = 2034                      -- failures being handed back for retry or dead-lettering

-- Why didn't my route fire?
EventId.Id = 2011 and RouteId = '<guid>'     -- per-route due evaluation (needs ScheduleDispatcher:HeartbeatLoggingEnabled)
EventId.Id = 2022                            -- time zone the host could not resolve (route OR workflow)
EventId.Id = 2003                            -- SystemSettings unreadable → portal-configured settings ignored
EventId.Id = 2002                            -- scheduler explicitly disabled

-- First-time setup / audit trail
EventId.Id = 1051                      -- connection test results (success and failure)
EventId.Id = 1041                      -- workflow definition saved, with the trigger it will run on
EventId.Id in [1002, 1021, 1031]       -- source connection / mapping profile / route saved

-- HTTP traffic (API)
RequestPath like '/api/v1/workflows%'
StatusCode >= 400
Elapsed > 2000                         -- slow requests, in ms
UserId = '<guid>'                      -- everything one admin did

-- Authentication and session
EventId.Id >= 7000 and EventId.Id < 8000     -- every auth / user-admin event
EventId.Id = 7002                            -- login failures, with the distinguishing reason
EventId.Id = 7003                            -- account lockouts (a burst across UserIds = credential stuffing)
UserId = '<guid>'                            -- one account: logins, lockouts, admin actions taken on it
EventId.Id in [7021, 7022, 7023]             -- user created / updated / role changed, with ActorUserId
EventId.Id = 7031                            -- first super-admin provisioned (expected once per environment)

-- Bulk export, which spans worker ticks and processes
EventId.Id in [4051, 4052]                   -- submitted / completed
EventId.Id = 3006                            -- run paused awaiting a bulk export job
EventId.Id = 3007                            -- run resumed after one

-- Everything that went wrong, across every stage
@Level in ['Error', 'Fatal']

-- First-time setup audit for a new tenant
EventId.Id >= 1000 and EventId.Id < 2000
```

`Application` distinguishes hosts (`FHIRBridge.Api` / `FHIRBridge.Worker`); `SourceContext` distinguishes the vendor
source executor, node executor or destination writer that emitted an event.

### HTTP request logging

`UseSerilogRequestLogging` in the API emits one event per request (method, path, status, duration) instead of the
framework's several lines. It is registered **before** the static-file, CORS/CSRF and authentication middleware so
it times the whole pipeline — a 401 that never reaches a handler is still logged. Levels are graded: `Error` at
5xx or an unhandled exception, `Warning` at 4xx, `Verbose` (i.e. dropped, given the Information minimum) for
health-check, Swagger and static-asset polling, `Information` otherwise.

Enrichment adds `RequestHost`, `RequestScheme`, `ClientIp`, `CorrelationId` and — when authenticated — `UserId`.
**Query strings are deliberately not enriched**: a FHIR search URL can carry patient identifiers, and
`PhiMaskingEnricher` matches on property *name*, so it would not redact them inside a single `QueryString` value.

### Authentication events and PHI

Auth events are keyed on **`UserId`**, never on the login address. `email` is in
`PhiRedactor.DefaultMaskedProperties`, so an `Email` property would render as `***` in every sink regardless —
and the address is already written to the governance `AuthenticationLog` table, which remains the compliance
trail. These events are the operational view on top of it.

Nothing credential-bearing is logged: no passwords or hashes, no MFA challenge tokens, no reset or invitation
tokens, no refresh tokens, no SSO bearer tokens. Failure *reasons* are logged server-side and are deliberately
more specific than the client response — the API keeps returning a generic "Invalid email or password" so it
cannot be used to enumerate accounts, while the log distinguishes a mistyped password from a disabled account,
an SSO-only account, or an address with no account at all.

### PHI

`PhiMaskingEnricher` masks PHI-named structured properties and adds redacted `MaskedExceptionMessage` /
`MaskedExceptionDetails` / `MaskedRenderedMessage` copies. That is a backstop, not a licence: the events added here
log **counts, identifiers, endpoints and timings only** — never resource payloads, never mapped record contents,
never tokens or secret values (only the vault/secret *name* that points at them).
