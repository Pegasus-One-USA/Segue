# FHIRBridge.Worker

> The background-processing host for FHIRBridge: a .NET Worker Service that runs the platform's long-lived and scheduled jobs — HL7 v2 MLLP intake, asynchronous pipeline-run and webhook-ingestion processing, schedule dispatching, and retention purging — outside the request/response path of the API.

**Layer:** Host (Worker) · **SDK:** Microsoft.NET.Sdk.Worker · **Target:** net9.0

## Purpose
`FHIRBridge.Worker` hosts the platform's `BackgroundService` workers. It composes the same Application/Infrastructure/Runtime layers as the API but with no HTTP user, running as a non-interactive system principal. It handles work that must not block API requests: receiving HL7 v2 messages over MLLP, consuming pipeline-run and webhook-ingestion commands from the messaging transport, periodically dispatching due scheduled runs, executing the runtime "Phase 1" scheduled pipeline, and purging expired data per the retention policy. It can run as a console app, a Linux daemon, or a Windows Service (`Microsoft.Extensions.Hosting.WindowsServices`).

## Responsibilities
- Bootstrap the generic host (`Program.cs` via `Host.CreateApplicationBuilder`) and register the layer DI extensions, options, worker security services, and the hosted `BackgroundService`s.
- Run as a non-interactive system identity (`SystemCurrentUserService`) so shared infrastructure services that expect a current user are satisfied without an HTTP context.
- Receive HL7 v2 messages over MLLP (TCP), process each through the HL7 message processor, and write framed ACKs back.
- Consume `PipelineRunCommand` and `WebhookIngestionCommand` messages from the transport and execute them through scoped handlers.
- On a timer, claim due scheduled runs and enqueue pipeline-run commands; separately, execute the runtime scheduled pipeline for due routes.
- Periodically run the retention purge over purgeable stores (the immutable HIPAA audit log is never purged).
- Be safely toggleable: every worker is **disabled by default** and gated by its own `*:Enabled` option, so the host can run only the roles a given deployment needs.

## Key components

### Host
- **Program.cs (composition root)** — builds the generic host; intended to register the Application/Infrastructure/Runtime DI extensions, options (`RuntimeWorkerOptions`, `ScheduleDispatcherOptions`, `RetentionPurgeOptions`, `Hl7MllpOptions`), the worker security services, and the hosted background services.
- **WorkerSecurityServices** — `SystemCurrentUserService` (`ICurrentUserService`) providing a non-authenticated `system:worker` principal, and `WorkerAccessTokenIssuer` (`IAccessTokenIssuer`) registered only to satisfy the DI graph (throws `NotSupportedException` if invoked — the Worker never issues tokens).

### Background services
- **Hl7MllpListenerService** — `BackgroundService` that opens a `TcpListener` and accepts HL7 v2 messages over MLLP. Buffers bytes, extracts complete MLLP frames (supports multiple back-to-back frames per connection via `Hl7MllpProtocol`), processes each through a scoped `Hl7MessageProcessor`, and writes back a framed ACK. Disabled by default; enable via `Hl7Mllp:Enabled` with `Port`, `TenantId`, `WebhookConfigurationId`.
- **PipelineRunCommandProcessor** — "processor" role; always-on consumer that idles until `PipelineRunCommand` messages arrive on the transport, then runs each via a scoped `IPipelineRunCommandHandler`. Maps to an event-triggered, queue-scaled job in Azure.
- **WebhookIngestionCommandProcessor** — consumes `WebhookIngestionCommand` messages and runs each via a scoped `IWebhookIngestionCommandHandler`. Active when the webhook API enqueues asynchronously over a shared transport (RabbitMQ / Azure Service Bus); with the in-process transport, API and Worker must share a process for the queue to be shared.
- **ScheduleDispatcherWorker** — "dispatcher" role; on a `PeriodicTimer` (min 15s) claims due scheduled runs via `IScheduleDispatcher` and enqueues pipeline-run commands for the processor role to execute. Disabled by default (`ScheduleDispatcher:Enabled`).
- **Worker** — the runtime "Phase 1" scheduled pipeline. On a `PeriodicTimer` (min 30s) resolves which configured routes are due (mode `ScheduledPull`/`WebhookAndScheduledPull`, schedule expression matches, and source/destination/mapping/webhook dependencies all enabled), then runs `IConfiguredPipelineService.StartAsync` for the due resource types. Disabled by default (`RuntimeWorker:Enabled`); requires `RuntimeWorker:TenantId`.
- **RetentionPurgeWorker** — on a `PeriodicTimer` (min 1h) runs `IRetentionPurgeService` to remove records older than the retention policy cutoff and logs a purge report. Disabled by default (`RetentionPurge:Enabled`).

### Options classes
- **RuntimeWorkerOptions** (+ `RuntimeWorkerSourceOptions`, `RuntimeWorkerDestinationOptions`) — toggles and parameters for the runtime scheduled pipeline: `Enabled`, `IntervalSeconds` (default 300), `TenantId`, `ResourceTypes` (defaults to all supported FHIR types), and source/destination descriptors (type, base URL, SMART backend-services auth fields, paging, connection string, schema).
- **ScheduleDispatcherOptions** — `Enabled`, `IntervalSeconds` (default 60) for the dispatcher role.
- **RetentionPurgeOptions** — `Enabled`, `IntervalHours` (default 24) for the purge worker.

## Dependencies
- **Projects:** `FHIRBridge.Application`, `FHIRBridge.Infrastructure`, `FHIRBridge.Runtime.Application`, `FHIRBridge.Runtime.Infrastructure`, `FHIRBridge.EventBus`, `FHIRBridge.Messaging`, `FHIRBridge.Observability` (BuildingBlocks).
- **Key packages:**
  - `Microsoft.Extensions.Hosting` — the generic host / `BackgroundService` infrastructure.
  - `Microsoft.Extensions.Hosting.WindowsServices` — run the host as a Windows Service.
  - `Serilog.Extensions.Hosting` — structured logging integrated with the generic host.

## Current state in this skeleton
Only a placeholder `Program.cs` exists — a minimal `Host.CreateApplicationBuilder` host that builds and runs but registers no services or hosted workers. The background services, worker security services, options classes, and the full DI composition described above are to be ported from the reference implementation at `…\StepBase\FHIRBridge\src\Worker\FHIRBridge.Worker`. The placeholder `Consumers/`, `BackgroundServices/`, `Processors/`, and `Schedulers/` folders declared in the `.csproj` indicate the intended organization. Project references and NuGet packages should match the reference.

## Roadmap — what it will do in detail
1. **Composition root.** Flesh out `Program.cs` to call the Application/Infrastructure/Runtime DI extension methods, register `SystemCurrentUserService` and `WorkerAccessTokenIssuer`, bind the options classes from configuration, configure Serilog, and add the hosted workers — each guarded so disabled roles register cheaply and exit early.
2. **HL7 v2 intake.** Port `Hl7MllpListenerService` and the `Hl7MllpOptions`; receive MLLP-framed HL7 v2 over TCP, hand each message to the scoped `Hl7MessageProcessor`, and return framed ACKs.
3. **Asynchronous command processing.** Port `PipelineRunCommandProcessor` and `WebhookIngestionCommandProcessor`; consume commands from the configured transport (`Messaging:Provider`) and execute via scoped handlers, decoupling long-running pipeline and webhook work from the API request thread.
4. **Scheduling.** Port `ScheduleDispatcherWorker` (claim-and-enqueue due runs) and the runtime `Worker` (execute due routes directly via `IConfiguredPipelineService`), with schedule-expression matching and route-dependency gating.
5. **Retention.** Port `RetentionPurgeWorker` to enforce the data-retention policy on purgeable stores while leaving the immutable HIPAA audit log untouched.
6. **Deployment topology (future).** Map the dispatcher/processor roles onto Azure Container Apps Jobs (scheduled dispatcher, queue-scaled processor), and support running as a Windows Service for on-prem/edge HL7 intake.
