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
