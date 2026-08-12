using FHIRBridge.Application;
using FHIRBridge.Infrastructure;
using FHIRBridge.Infrastructure.Messaging;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Persistence.Workflows;
using FHIRBridge.Infrastructure.Security;
using FHIRBridge.Observability;
using FHIRBridge.Observability.Logging;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows;
using FHIRBridge.Worker;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

// No-ops unless actually launched by that OS's service manager — lets the same published output
// run as a systemd service on Linux or a Windows Service, with `dotnet run` unaffected.
builder.Services.AddWindowsService(options => options.ServiceName = "FHIRBridge.Worker");
builder.Services.AddSystemd();

// HostApplicationBuilder has no .Host.UseSerilog() (that's WebApplicationBuilder-only, via Serilog.AspNetCore) — build
// the shared logger directly and register it as the logging provider instead.
var serilogLogger = new LoggerConfiguration()
    .ConfigureFhirBridge(builder.Configuration, "FHIRBridge.Worker")
    .CreateLogger();
builder.Logging.ClearProviders();
builder.Logging.AddSerilog(serilogLogger, dispose: true);

// DbSecretStore (app-provisioned secrets, e.g. destination connection strings written by the Api's wizard) encrypts
// at rest via Data Protection — a real runtime dependency of the shared configuration/secret-resolution graph. The
// Worker MUST resolve the SAME key ring as the Api or it can't decrypt what the Api wrote (and vice versa), so this
// mirrors FHIRBridge.Api/Program.cs exactly: same application name + same KeyRingPath (a shared path in prod, or the
// same stable machine-local default when unset). Never an ephemeral ring.
var workerDataProtection = builder.Services.AddDataProtection()
    .SetApplicationName(builder.Configuration["DataProtection:ApplicationName"] ?? "FHIRBridge");

var workerKeyRingPath = builder.Configuration["DataProtection:KeyRingPath"];
if (string.IsNullOrWhiteSpace(workerKeyRingPath))
{
    workerKeyRingPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FHIRBridge",
        "dataprotection-keys");
}

Directory.CreateDirectory(workerKeyRingPath);
workerDataProtection.PersistKeysToFileSystem(new DirectoryInfo(workerKeyRingPath));

// Same reasoning as the Api host — see its Program.cs comment. AddAspNetCoreInstrumentation() is a no-op here
// (no ASP.NET Core pipeline in this host), but HttpClient/.NET-runtime/custom-meter instrumentation still applies.
builder.Services.AddFhirBridgeObservability(builder.Configuration, "FHIRBridge.Worker");

builder.Services
    .AddFHIRBridgeApplication()
    .AddFHIRBridgeInfrastructure(builder.Configuration)
    .AddWorkflowCore()
    .AddWorkflowInfrastructure()
    .AddWorkflowSqlPersistence(builder.Configuration);

builder.Services.Configure<RuntimeWorkerOptions>(builder.Configuration.GetSection("RuntimeWorker"));
builder.Services.AddHostedService<Worker>();

// Scheduling migration (2026-07-18): the queue-based path is now the live scheduler by default —
// ScheduleDispatcherWorker atomically claims due routes (IScheduleEvaluationService.ClaimDueRunsAsync, verified
// safe under concurrent instances via ResourcePipelineRoute's RowVersion optimistic-concurrency token) and enqueues
// a PipelineRunCommand; PipelineRunCommandProcessor consumes it. Worker.RunDueRoutesAsync (direct-call polling) is
// now off by default — see RuntimeWorkerOptions.DirectRouteSchedulingEnabled — kept only as a fast-rollback switch.
builder.Services.Configure<ScheduleDispatcherOptions>(builder.Configuration.GetSection("ScheduleDispatcher"));
builder.Services.AddHostedService<ScheduleDispatcherWorker>();
builder.Services.AddHostedService<PipelineRunCommandProcessor>();

// Field-level lineage capture: MappingNodeExecutor buffers per-hop lineage in-memory during transform execution
// and publishes a LineageCaptureCommand per resource rather than writing FieldLineageEntries synchronously — this
// processor is where that write actually happens, off the transform pipeline's own execution path. Also
// registered in the Api host (see its Program.cs) — a manual/interactive workflow run executes synchronously
// inside Api, and the InMemory transport is in-process-only, so Api needs its own local consumer too.
builder.Services.AddHostedService<LineageCaptureProcessor>();

// Closes a separate, previously-silent gap found while reviewing the scheduling path: WebhookIngestionController
// (Api host) already enqueues a WebhookIngestionCommand on every inbound webhook via IWebhookIngestionDispatcher —
// with no consumer running, those were silently never processed in any transport configuration. No double-claim
// race to resolve here; registering this was always safe, it just hadn't been done.
builder.Services.AddHostedService<WebhookIngestionCommandProcessor>();

// Resumable bulk-export polling: BulkExportPollWorker checks every in-flight $export job's status on a timer
// instead of any caller (a route run, a workflow-node run) blocking inline for the job's full duration.
builder.Services.Configure<BulkExportPollOptions>(builder.Configuration.GetSection("BulkExportPoll"));
builder.Services.AddHostedService<BulkExportPollWorker>();

builder.Services.Configure<EndpointHealthCheckOptions>(builder.Configuration.GetSection("EndpointHealthCheck"));
builder.Services.AddHostedService<EndpointHealthCheckWorker>();
builder.Services.AddHostedService<LoincSynchronizationWorker>();

// Retention enforcement: was built (RetentionPurgeService/ConfiguredRetentionPolicyService/the purgeable-store
// registrations in AddFHIRBridgeInfrastructure) but never actually hosted anywhere until now, so it never ran.
// Enabled by default (RetentionPurgeOptions.Enabled = true) — this starts genuinely deleting expired rows from
// every registered IPurgeableStore on a 24h timer. The four immutable HIPAA audit tables are never purgeable
// (see GovernanceLogPurgeableStore's remarks) regardless of this setting.
builder.Services.Configure<RetentionPurgeOptions>(builder.Configuration.GetSection("RetentionPurge"));
builder.Services.AddHostedService<RetentionPurgeWorker>();

// Recurring AuditLog hash-chain tamper check — previously only verified on-demand inside Compliance Report
// generation. Enabled by default; raises a Critical SecurityEvent if the chain is ever found broken.
builder.Services.Configure<AuditChainVerificationOptions>(builder.Configuration.GetSection("AuditChainVerification"));
builder.Services.AddHostedService<AuditChainVerificationWorker>();

// Alert Engine: evaluates every enabled AlertRule against SecurityEvents on a timer, firing real alerts
// (AlertHistoryEntry + email) — see IAlertEvaluationService.
builder.Services.Configure<AlertEvaluationOptions>(builder.Configuration.GetSection("AlertEvaluation"));
builder.Services.AddHostedService<AlertEvaluationWorker>();

// AddFHIRBridgeApplication/Infrastructure register the full application surface (auth, user management, etc.) that
// only the Api host actually wires end-to-end (IDataProtectionProvider, IAccessTokenIssuer, ...). The Worker never
// resolves those services — it only uses IConfigurationRepository/IConfiguredPipelineService/the workflow store — so
// skip Development's default ValidateOnBuild, which otherwise fails the whole host over unrelated, unused services.
builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
{
    ValidateOnBuild = false,
    ValidateScopes = builder.Environment.IsDevelopment(),
}));

var host = builder.Build();

// Fail fast rather than silently double-dispatch: direct-call route polling (Worker.RunDueRoutesAsync) and the
// queue-based dispatcher (ScheduleDispatcherWorker) use unrelated due-detection/claim logic — running both at
// once has no protection against processing the same due routes twice. See RuntimeWorkerOptions.
// DirectRouteSchedulingEnabled's remarks; this should only ever be true as a deliberate, temporary rollback with
// ScheduleDispatcher:Enabled explicitly turned off first.
{
    var runtimeWorkerOptions = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RuntimeWorkerOptions>>().Value;
    var scheduleDispatcherOptions = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ScheduleDispatcherOptions>>().Value;
    if (runtimeWorkerOptions.DirectRouteSchedulingEnabled && scheduleDispatcherOptions.Enabled)
    {
        throw new InvalidOperationException(
            "RuntimeWorker:DirectRouteSchedulingEnabled and ScheduleDispatcher:Enabled are both true. " +
            "Running both scheduling paths at once can double-dispatch the same due routes — disable one before starting. " +
            "See RuntimeWorkerOptions.DirectRouteSchedulingEnabled's remarks.");
    }
}

// Applies pending EF migrations, mirroring the Api host's bootstrap — no-ops on the in-memory path (no
// ConnectionStrings:FHIRBridgeDb configured, so AddFHIRBridgeInfrastructure never registers FHIRBridgeDbContext).
using (var scope = host.Services.CreateScope())
{
    scope.ServiceProvider.GetService<FHIRBridgeDbContext>()?.Database.Migrate();
}

// Ensures the download-link signing secret exists (generating it on first boot if needed) — the Worker can
// mint download links (DownloadUrlDeliveryStrategy) that the Api host later verifies, so both hosts must
// resolve the same value; sharing the DB-provisioned secret (and Data Protection key ring) is what makes
// that safe. See AppSecretProvisioner's remarks. Must run after the migration above.
AppSecretProvisioner.ProvisionAsync(host.Services, CancellationToken.None).GetAwaiter().GetResult();

host.Run();
