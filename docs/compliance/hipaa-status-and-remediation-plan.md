# HIPAA Compliance — Status, Gaps & Remediation Plan

**Basis:** direct source-code audit of the FHIRBridge repository (API, Worker, Runtime, Infrastructure, Angular portal), performed 2026-08-17.
**Relation to other compliance docs:** this file sits alongside [`hipaa-soc2-control-matrix.md`](./hipaa-soc2-control-matrix.md) and the rest of this folder. Where this audit found the control matrix's "Implemented" status doesn't match what the code actually does, it's called out explicitly below under [Corrections to the existing control matrix](#corrections-to-the-existing-control-matrix) — that file should be updated once the fixes here land.
**Not a substitute for:** a formal HIPAA risk assessment, a penetration test, or legal review. Where a pending item is organizational rather than technical, it links to the existing template in this folder.

---

## 1. What HIPAA requires, in short

FHIRBridge processes ePHI as a **Business Associate** of the covered entities (health systems) it integrates with. That triggers the full HIPAA Security Rule regardless of whether data is persisted long-term — transient/pass-through ETL processing still counts as "maintaining or transmitting" PHI. Three safeguard categories apply:

- **Technical safeguards** (§164.312) — access control, audit control, integrity, transmission security. This is where almost all of the engineering work below lives.
- **Administrative safeguards** (§164.308) — risk analysis, workforce training, sanction policy, assigned security responsibility. Already scaffolded as templates in [`policies/`](./policies/) and [`risk-assessment/`](./risk-assessment/) — these need an owner and leadership sign-off, not code.
- **Physical safeguards** (§164.310) — facility/device security. Out of scope for this repository entirely; owned by whoever operates the hosting infrastructure.

HIPAA has no certificate. "Compliant" means operating all of the above continuously and being able to prove it — which is why the organizational items below matter as much as the code fixes.

---

## 2. What we have already done

Verified directly in this audit, by reading the actual implementation (not just documentation):

| Control | Evidence |
|---|---|
| RBAC on sensitive endpoints | `PipelineRunsController`, `ConfigurationsController`, `AppSecretsController` all require `[Authorize]` + specific permission policies. No unauthenticated PHI path found. |
| Unique user identification | No shared/generic service accounts found anywhere in the API. Every authenticated action maps to a real user identity, stamped into the audit chain. |
| Strong password policy | `LocalAuthService` enforces 12+ characters with mixed character classes. |
| Automatic logoff | Portal: real 30-minute idle timeout tied to DOM activity (`session.service.ts`), clears tokens and forces re-auth. API: 60-minute access tokens in production config. |
| CORS locked down | DB-backed origin allowlist (`DynamicPortalCorsPolicyProvider`); no `AllowAnyOrigin` anywhere in the repo. |
| Durable, tamper-evident audit log (config changes) | `AuditLog` entity implements a real SHA-256 hash chain with an append-only DB guard (`AuditingSaveChangesInterceptor` throws on update/delete). Currently wired to `BulkExportJob`, `TransformationRule`, `SchemaMapping`, `WorkflowDefinition`. |
| HTTPS enforcement | `UseHttpsRedirection`/`UseHsts`/`RequireHttpsMetadata` all correctly gated to non-Development environments. |
| Encrypted SFTP delivery | Uses an SSH channel (SSH.NET) with credentials pulled from the secret store, not inline. |
| Clean tracing | OpenTelemetry instrumentation captures no request/response bodies, headers, or SQL parameter values — only correlation IDs. |
| TDE treated correctly as infra-level | A startup health check flags (rather than silently ignores) an unencrypted database, without trying to configure TDE from application code. |
| Some encryption-at-rest for tokens | The OAuth authorization-code store (`DistributedFhirAuthorizationCodeTokenStore`) correctly wraps values with `IDataProtector` before writing to Redis. |
| Organizational scaffolding | Full HIPAA/SOC 2 policy set, risk register, BAA template, pen-test plan, and SOC 2 readiness assessment already drafted in this folder (see the [program README](./README.md)) — these need owners and signatures, not authoring. |

---

## 3. Corrections to the existing control matrix

[`hipaa-soc2-control-matrix.md`](./hipaa-soc2-control-matrix.md) marks a few things "Implemented" that this audit found are either not wired up, or only partially wired up, in the actual code. These should be corrected in that file once fixed here, so the matrix stays trustworthy for an auditor:

- **"Secrets encrypted in Azure Key Vault"** — Key Vault resolution code is real (`AzureKeyVaultSecretProvider`), but it only activates when `KeyVault:UseAzureKeyVault=true`, which is **not set in any checked-in configuration**. By default, destination secrets resolve through `DbSecretStore` — a Data-Protection-encrypted value in the application database, not an actual vault. Not a plaintext leak, but "Key Vault" is currently aspirational unless explicitly turned on per deployment.
- **"Safe Harbor + k-anonymity de-identification ... before data leaves the trust boundary where applicable"** — the de-identification services are real and functional, but the only two registered governance rules always return `requiresDeIdentification: false`. **De-identification never actually executes today**, for any destination. This is the most significant correction — the matrix currently implies a working PHI-minimization control that isn't active.
- **"`PhiMaskingEnricher` masks PHI before logs are written"** — true for the cases it covers, but it only inspects top-level log properties by exact key name. It doesn't recurse into destructured objects (`{@Patient}`) and never inspects exception messages or stack traces. Separately, `GlobalExceptionManager` writes raw exception text straight to the `ErrorLogs` table through a path that bypasses this enricher entirely. The control exists but has real gaps.
- **Audit Controls / `DataAccessLog`** — the matrix describes a `DataAccessLog` capturing "every patient/resource governance decision." This audit separately found an `AuthorizationLog` that is documented and built to record **denials only**, not successful PHI reads. If `DataAccessLog` is a distinct table that does cover successful access, reconcile which one is authoritative; if they're the same mechanism, the matrix overstates coverage.
- **Runtime pipeline audit trail** — not mentioned in the matrix at all. `IWorkflowAuditRecorder` is currently `InMemoryWorkflowAuditRecorder`, a plain in-process list that's never persisted. PHI-touching pipeline extraction/transform/output events in the Runtime plane leave no durable audit record, despite the durable hash-chained `AuditLog` mechanism existing and working well for config entities.

---

## 4. What is pending

> **On the word "certification":** HIPAA has no certificate, seal, or pass/fail exam — "compliant" means operating the required safeguards continuously and being able to prove it to an auditor or OCR investigator. The **Must** tag below means "required implementation specification, or so directly tied to one that skipping it leaves a real gap an investigator would cite." **Should** means an addressable specification or best-practice hardening — a risk-based judgment call is legitimate, but skipping it should be a documented decision, not an oversight. **Optional** means it's good hygiene but not something HIPAA itself asks for. These are engineering-informed judgment calls, not a legal determination — have compliance counsel confirm the tag on anything you plan to formally document as "addressed, not applicable."

**13 of 28 items below are tagged Must** (8 of 21 technical, 5 of 7 organizational) — those are the ones that actually block a defensible compliance posture. The rest are real improvements, but skipping them is a risk-acceptance decision, not a compliance failure.

### 4a. Technical gaps (engineering — this repo)

Ordered by exposure; the first four should be closed before any real patient data flows through a production deployment.

| # | Gap | Severity | Cert |
|---|---|---|---|
| 1 | RSA private key committed to `appsettings.Development.json` | High | **Must** |
| 2 | De-identification governance rule never sets `requiresDeIdentification: true` | High | Should |
| 3 | FHIR access tokens cached in Redis as plaintext | High | **Must** |
| 4 | Runtime pipeline audit trail is in-memory only | High | **Must** |
| 5 | Webhook ingestion messages carry full raw FHIR JSON on the queue | High | **Must** |
| 6 | Generated CSV/PDF exports written unencrypted to disk, no active purge | High | **Must** |
| 7 | Portal stores JWTs in localStorage/sessionStorage | High | Should |
| 8 | HL7 MLLP listener has no TLS transport option | High | **Must** (before enabling) |
| 9 | PHI-masking enricher doesn't recurse into nested objects or scan exceptions | Medium | Should |
| 10 | `GlobalExceptionManager` bypasses the masking enricher | Medium | Should |
| 11 | Data Protection key ring stored unprotected on local disk | Medium | Should |
| 12 | Password rotation field (`PasswordExpiresOnUtc`) defined but never enforced | Medium | Should |
| 13 | Authorization log records denials only, not successful PHI access | Medium | Should |
| 14 | RabbitMQ dead-letter queues have no TTL/max-length | Medium | Should |
| 15 | Redis/RabbitMQ have no TLS enforced in code (deployment-dependent) | Medium | **Must** |
| 16 | Connection secrets and PII logged to the browser console (portal) | Medium | **Must** |
| 17 | Field-mapping snapshots persist indefinitely in portal localStorage | Medium | Should |
| 18 | Forced password-change claim can be stale for a token's full lifetime | Low | Should |
| 19 | Development token lifetime extended to 8 hours | Low | Should |
| 20 | Auth interceptor attaches bearer token without a scheme (http/https) check | Low | Should |
| 21 | Footer discloses build/version tag on the public login page | Low | Optional |

**Why these eight are Must:** #1, #3, #5, #6, #15, and #16 are live or easily-triggered exposures of credentials, tokens, or PHI that a reasonable-safeguards standard doesn't tolerate regardless of "addressable" language. #4 is a direct gap against the **Required** Audit Controls specification (§164.312(b)) — there is currently no durable record of PHI-touching pipeline runs at all. #8 is Must only in the sense that it must be fixed *before* the MLLP listener is ever turned on; it's inert today.

**Why #2 and #7 are Should, not Must:** De-identification (#2) is a risk-reduction technique, not a named Required specification — as long as the identified data reaching a destination is properly access-controlled, encrypted in transit, and covered by a BAA, sending it identified isn't itself a violation. The documentation-accuracy problem (the control matrix implying de-identification is active when it isn't) is the more urgent part — that should be corrected immediately even though implementing real de-identification is a policy-paced project. Portal token storage (#7) is a real hardening improvement against XSS, but many organizations run identity systems on `localStorage` under compensating controls (CSP, short token lifetimes) — a documented risk-acceptance is defensible here in a way it isn't for #1 or #3.

### 4b. Organizational items (already scaffolded in this folder — need ownership, not authoring)

| Item | Where the template already lives | Cert |
|---|---|---|
| Assign Security Official & Privacy Official | [`policies/02-assigned-security-responsibility.md`](./policies/02-assigned-security-responsibility.md) | **Must** — named Required specification, §164.308(a)(2) |
| Adopt the full policy set via leadership | [`policies/`](./policies/) (15 documents) | **Must** — several contained policies (security management process, sanction policy, information system activity review) are individually Required |
| Complete and ratify the risk analysis | [`risk-assessment/`](./risk-assessment/) | **Must** — Required, §164.308(a)(1)(ii)(A); the single most-cited control in OCR enforcement actions |
| Execute BAAs with Azure and every subprocessor | [`baa/`](./baa/), tracked in [`baa/subprocessor-register.md`](./baa/baa-tracking-checklist.md) | **Must** — Required by statute, §164.308(b)(1) and §164.502(e); not a risk-based judgment call |
| Implement and test backup/DR | [`backup-disaster-recovery.md`](./backup-disaster-recovery.md) | **Must** — the Data Backup Plan and Disaster Recovery Plan within Contingency Planning (§164.308(a)(7)) are Required; the testing/revision procedure underneath is Addressable |
| Engage a firm for penetration testing | [`pen-test/`](./pen-test/) | Should — satisfies the Required "Evaluation" standard (§164.308(a)(8)), but the standard doesn't name penetration testing specifically; any periodic technical evaluation can satisfy it |
| SOC 2 readiness → Type I → Type II | [`soc2/`](./soc2/) | Optional — SOC 2 is a separate, voluntary attestation framework, not a HIPAA requirement at all |

These follow the sequencing already laid out in the [program README's roadmap](./README.md#recommended-execution-roadmap) — nothing new to add here; they're listed for completeness against "what's pending."

---

## 5. How to do each pending item

### Technical fixes

**1. Committed private key** `MUST` — *Files: `src/Api/FHIRBridge.Api/appsettings.Development.json`, `src/Worker/FHIRBridge.Worker/appsettings.Development.json`*
1. Treat this as a live secret exposure, not a normal bug — open an incident, don't just fix-forward silently.
2. Rotate the key pair with Epic (generate a new key, register the new public key/JWKS with Epic's App Orchard config, deprecate the old one on their side).
3. Purge the old key from git history with `git filter-repo --path src/Api/FHIRBridge.Api/appsettings.Development.json --invert-paths` (or BFG Repo-Cleaner) — a plain new commit isn't enough, the key stays readable in history.
4. Force-push the cleaned history only after coordinating with every branch owner (this rewrites SHAs); if that's too disruptive, rotating the key at Epic is the load-bearing fix and history-scrubbing can follow separately.
5. Replace the checked-in value with a placeholder and load the real key from user-secrets locally (`dotnet user-secrets set`) or Key Vault in shared environments.
6. Add a secret-scanning pre-commit hook (`gitleaks protect --staged`) and a CI job (`gitleaks detect`) so a future commit can't reintroduce a key silently.
7. **Verify:** `git log -p -- '*appsettings.Development.json'` no longer shows the private key in any commit reachable from `main`.

**2. De-identification never triggers** `Should` — *Files: `src/FHIRBridge.Application/Services/*GovernanceRule.cs`, `ConfiguredPipelineService.cs:839`, `DestinationConfiguration` entity*
1. This is a policy decision first — sit down with compliance and decide which destination types/tenants require de-identification (e.g., analytics/BI exports vs. clinical write-back destinations that need identified data to function).
2. Add a `RequiresDeIdentification` (and optionally `DeIdentificationMethod`: SafeHarbor/KAnonymity) flag to the `DestinationConfiguration` aggregate, with an EF Core migration.
3. Expose the flag in the portal's destination-configuration wizard so tenant admins can set it per destination.
4. Implement a new `IGovernanceRule` (e.g. `DestinationSensitivityGovernanceRule`) that reads this flag and returns `GovernanceRuleResult` with `requiresDeIdentification: true` when set — register it alongside `ResourceTypeAccessGovernanceRule`/`ConsentGovernanceRule` in `FHIRBridge.Application/DependencyInjection.cs`.
5. Confirm `ConfiguredPipelineService` correctly routes to `SafeHarborDeIdentificationService`/`KAnonymityDeIdentificationService` when the flag is true — both already work, they just need a caller.
6. Add an integration test asserting a destination flagged `RequiresDeIdentification` never receives an identified field (name, MRN, DOB down to the day, etc.).
7. Get compliance sign-off on the chosen method per destination (Safe Harbor is self-certifiable; k-anonymity/Expert Determination needs a qualified statistician's review) before relying on it in production.
8. **Verify:** trigger a pipeline run against a de-identification-flagged destination in a test tenant and inspect the delivered payload for absence of direct identifiers.

**3. Plaintext tokens in Redis** `MUST` — *File: `DistributedFhirAccessTokenCache.cs:23-42`, pattern to copy: `DistributedFhirAuthorizationCodeTokenStore.cs:25-33`*
1. Inject `IDataProtector` (create a dedicated purpose string, e.g. `"FhirAccessTokenCache.v1"`) into `DistributedFhirAccessTokenCache`.
2. Wrap the token/scope payload with `Protect(...)` before `IDistributedCache.SetAsync`, and `Unprotect(...)` after `GetAsync`.
3. Handle `CryptographicException` on unprotect as a cache miss (treat as "re-authenticate"), consistent with how `AppSecretProvisioner` already self-heals on key-ring rotation.
4. Add a unit test confirming the raw bytes written to the underlying `IDistributedCache` mock never contain the plaintext token string.
5. **Verify:** `redis-cli GET <key>` against a dev Redis instance shows ciphertext, not a readable JWT.

**4. In-memory workflow audit** `MUST` — *Files: `WorkflowServiceCollectionExtensions.cs:19`, `Audit/InMemoryWorkflowAuditRecorder.cs`, reuse pattern from `AuditLog`/`AuditingSaveChangesInterceptor`*
1. Design a `WorkflowAuditEntry` table (actor, workflow run ID, node/step, action, resource type, timestamp, correlation ID — no PHI payload) mirroring the existing `AuditLog` hash-chain shape.
2. Add the EF Core migration and register the entity as an `IAppendOnlyEntity` so the existing `AuditingSaveChangesInterceptor` guard applies for free.
3. Implement `DbWorkflowAuditRecorder : IWorkflowAuditRecorder` that writes through this table instead of an in-memory list.
4. Swap the DI registration in `WorkflowServiceCollectionExtensions.cs` from `InMemoryWorkflowAuditRecorder` to the new durable implementation.
5. Surface these entries in the existing `GET /api/v1/governance/reports/hipaa-audit` evidence report alongside config-change audit rows, so pipeline-run evidence is exportable the same way.
6. **Verify:** run a workflow, restart the API process, confirm the audit rows for that run are still queryable (proving they survived past the DI container's lifetime, unlike the in-memory list).

**5. Raw PHI on the message queue** `MUST` — *Files: `WebhookIngestionCommand.cs:9`, `RabbitMqPublisher.cs:25`, `AzureServiceBusDispatchers.cs:35-38`, `RabbitMqTopology.cs`*
1. Confirm (don't assume) that every environment's RabbitMQ connection uses `amqps://` and every Service Bus connection uses its default TLS transport — see item 15 for the enforcement mechanism.
2. Lock down RabbitMQ vhost permissions so only the ingestion publisher and its consumer can read the `webhook-ingestion` queue (least-privilege, not a shared "app" user with access to every queue).
3. Apply the DLQ retention fix (item 14) to this queue specifically, since it's the one confirmed to carry raw PHI.
4. Record the message broker (RabbitMQ host / Azure Service Bus namespace) as a subprocessor in `baa/subprocessor-register.md` if it's hosted by a third party, since it transmits PHI.
5. **Verify:** attempt to connect to the queue with a credential outside the ingestion service's identity and confirm it's denied.

**6. Unencrypted generated files** `MUST` — *Files: `GeneratedFileDownloadLinkService.cs:42-51` (`CreateLinkAsync`), lines 78-85 (`TryResolveAsync`), consumers: `MappedCsvDestinationWriter.cs`, `DownloadUrlDeliveryStrategy.cs`*
1. Encrypt file bytes with AES-GCM using a key sourced from Key Vault or the existing Data Protection key ring before `File.WriteAllBytesAsync`.
2. Decrypt on read in `TryResolveAsync`, immediately before streaming the file back to the requester.
3. Add a `BackgroundService` (e.g. `ExpiredGeneratedFilePurgeJob`) that scans the storage folder on a timer (e.g. every 15 minutes) and deletes anything past its expiry, instead of relying on someone requesting an expired link to trigger cleanup.
4. Register the new job in `IPurgeableStore`'s existing registration list (`DependencyInjection.cs:201-226`) so it's covered by whatever operational monitoring already watches that list.
5. Add an integration test that creates a file, fast-forwards past expiry (or sets a 0-second TTL), runs the purge job, and asserts the file is gone from disk.
6. **Verify:** inspect the storage folder on disk directly and confirm file contents are ciphertext, and that no file older than its TTL remains after the purge job runs.

**7. Portal token storage** `Should` — *File: `portal/src/app/auth/services/token.service.ts`, plus `AuthController`, `auth.interceptor.ts`*
1. Update the API's login/refresh endpoints to set the access and refresh tokens via `Set-Cookie` with `HttpOnly; Secure; SameSite=Strict` instead of returning them in the JSON body.
2. Update the Angular `HttpClient` calls to send `withCredentials: true` so the browser attaches the cookie automatically; remove the manual `Authorization` header attachment from `auth.interceptor.ts` for cookie-backed requests.
3. Because cookie-based auth reintroduces CSRF risk, add a CSRF token (double-submit cookie or synchronizer token) for state-changing requests.
4. Update `token.service.ts` to stop reading/writing `localStorage`/`sessionStorage` for the tokens themselves; keep only non-sensitive UI state (e.g. "last active tenant") there if needed.
5. Update logout to call a server endpoint that clears the cookie (`Set-Cookie` with `Max-Age=0`), not just a client-side storage clear.
6. Re-verify CORS: `AllowCredentials()` is already enabled (see the audit's "already done" list), so this change is compatible with the current CORS policy.
7. **Verify:** after login, open browser dev tools → Application → Storage and confirm no JWT appears in `localStorage`/`sessionStorage`; confirm the cookie is flagged `HttpOnly` (JavaScript `document.cookie` can't read it).

**8. MLLP listener has no TLS** `MUST (before enabling)` — *File: `Worker/Hl7MllpListenerService.cs`, config: `Hl7MllpOptions`*
1. Add certificate configuration to `Hl7MllpOptions` (certificate path/thumbprint, or Key Vault reference).
2. Wrap the accepted `TcpClient`'s stream in an `SslStream` and call `AuthenticateAsServerAsync` with the configured certificate before handing the stream to the existing MLLP frame parser.
3. Optionally support mutual TLS (client certificate validation) if the sending EHR/interface engine supports it, for stronger authentication than IP allowlisting alone.
4. Add an integration test using a self-signed test certificate that connects with `SslStream` client-side and confirms a plaintext connection is rejected.
5. Since this listener isn't registered as a hosted service today, land this fix *before* anyone enables `Hl7MllpOptions.Enabled` in a real config — don't let this ship as a follow-up after the feature goes live.
6. **Verify:** a plaintext TCP client attempting the MLLP handshake without TLS is refused; a TLS-wrapped client completes the handshake and exchanges a test HL7 message.

**9. PHI-masking enricher gaps** `Should` — *File: `src/BuildingBlocks/FHIRBridge.Observability/Logging/FhirBridgeLogging.cs:88-119`*
1. Extend the enricher's property-walking logic to recurse into `StructureValue` properties (Serilog's representation of `{@Foo}` destructured objects), not just top-level `LogEvent.Properties`.
2. Add exception scanning: when `LogEvent.Exception` is non-null, run the same key/value masking logic (or a regex-based scan for SSN/MRN-shaped patterns) against `Exception.Message` and `Exception.ToString()` before the event reaches any sink.
3. Consider also scanning the rendered message text (`LogEvent.RenderMessage()`) for interpolated values that bypassed structured properties entirely.
4. Add unit tests: one logging a destructured object containing a nested `Ssn` field, one logging an exception whose `Message` embeds a fake MRN — both should come out masked.
5. **Verify:** the new unit tests pass, and a manual test logging `_logger.LogError(ex, "failed for {@Patient}", patientWithSsn)` produces a masked value in the Seq/console output.

**10. `GlobalExceptionManager` bypasses masking** `Should` — *File: `src/BuildingBlocks/FHIRBridge.Governance/GlobalExceptionManager.cs:50-69`*
1. Extract the masking logic from the Serilog enricher into a shared, standalone service (e.g. `IPhiRedactor`) that both the enricher and `GlobalExceptionManager` can call — avoids duplicating masking rules in two places that can drift apart.
2. Call `IPhiRedactor.Redact(exception.Message)` / `Redact(exception.ToString())` before persisting to `ErrorLogs` in `IGovernanceLogger.LogErrorAsync`.
3. Add a unit test asserting an exception message containing a fake identifier is redacted in the persisted `ErrorLogs` row.
4. **Verify:** trigger an exception with a known fake PHI value in its message and confirm the `ErrorLogs` table shows the redacted form, not the raw value.

**11. Unprotected Data Protection key ring** `Should` — *Files: Api `Program.cs:97`, Worker `Program.cs:50`, config: `DataProtection:KeyRingPath`*
1. Provision an X.509 certificate for Data Protection key encryption (self-signed for dev/test, a real cert or Key Vault-managed cert for staging/production).
2. Chain `.ProtectKeysWithCertificate(cert)` onto the existing `AddDataProtection()` call, or switch to `.ProtectKeysWithAzureKeyVault(keyIdentifier, credential)` for a fully managed option.
3. For multi-instance deployments, pair this with a shared persistence location (`PersistKeysToAzureBlobStorage`) instead of the local filesystem, so every instance decrypts the same key ring.
4. Keep the existing `AppSecretProvisioner` self-heal behavior as the fallback path if a key ever can't be decrypted (e.g. cert rotated without coordination) — it already handles this gracefully for the secrets it manages.
5. **Verify:** inspect the persisted key XML directly — it should be encrypted (unreadable) rather than plain XML, and a second instance pointed at the same shared store can decrypt tokens issued by the first.

**12. Password rotation not enforced** `Should` — *File: password-change-required gate middleware in `Program.cs` (~lines 475-500), entity: `User.PasswordExpiresOnUtc`*
1. Decide the actual rotation policy (e.g. 90 days) and record it in `policies/09-access-control-and-authentication.md`.
2. In the existing gate middleware — the same one that checks `MustChangePassword`/`mfa_setup_required` — add a check for `User.PasswordExpiresOnUtc < DateTime.UtcNow` and require a password change when true.
3. Add a scheduled job or login-time check that warns users N days before expiry (better UX than a hard block with no warning).
4. If, after discussion, rotation isn't actually a policy goal (NIST 800-63B has moved away from mandatory rotation in favor of breach-triggered resets) — explicitly decide that and remove the dead `PasswordExpiresOnUtc` field rather than leaving it as misleading unused state.
5. **Verify:** set a test user's `PasswordExpiresOnUtc` to a past date and confirm their next authenticated request is redirected to the password-change flow.

**13. Authorization log records denials only** `Should` — *File: `AuthorizationLog.cs`, related: possible `DataAccessLog` (see §3 correction)*
1. First reconcile with the control matrix's `DataAccessLog` reference — confirm whether that table already exists and covers successful access, or whether it's aspirational.
2. If it doesn't exist, add a lightweight positive-access record (actor, resource type, resource ID, action=Read, timestamp — no PHI payload) written alongside the existing denial-only `AuthorizationLog`, at the same enforcement point (the authorization handler) so both allow and deny paths are covered by one hook.
3. Be deliberate about volume — logging every single field read on high-traffic endpoints can be expensive; consider logging at the resource-access level (e.g. "read Patient/123"), not per-field.
4. Add a retention/purge policy for this table consistent with `docs/compliance/policies/13-data-retention-and-disposal.md`.
5. **Verify:** a successful PHI read produces a queryable log row with actor + resource ID + timestamp, with no patient data embedded in the row itself.

**14. RabbitMQ DLQs have no retention** `Should` — *File: `RabbitMqTopology.cs:28-52`*
1. Add `x-message-ttl` (e.g. 30 days, aligned with your incident-response SLA) and `x-max-length` arguments to the `.dlq` queue declaration.
2. Add monitoring/alerting on DLQ depth so messages are triaged by a human before they age out, rather than silently expiring.
3. Separately confirm the Azure Service Bus DLQ retention policy in the Bicep provisioning (external to this repo) matches the same retention intent.
4. **Verify:** publish a message designed to fail processing, confirm it lands in the DLQ, and confirm the queue's declared arguments show the TTL/max-length via `rabbitmqctl list_queues` or the management UI.

**15. No enforced TLS to Redis/RabbitMQ in code** `MUST` — *File: `DependencyInjection.cs:71-83` (Redis), connection-string configuration generally*
1. Add a startup validation check (fail-fast, not just a log warning) that rejects a Redis connection string without `ssl=true`/`rediss://` when `ASPNETCORE_ENVIRONMENT` is not `Development`.
2. Do the same for the RabbitMQ connection string — require `amqps://` outside Development.
3. Update the production configuration template/deployment docs to make this the default expectation, not something each deployer has to remember.
4. **Verify:** attempt to start the API in a `Production`-flagged environment with a plain `redis://` connection string and confirm startup fails with a clear error rather than silently connecting in plaintext.

**16. Console-logged secrets/PII in the portal** `MUST` — *Files: `ehr-vendor-source-form.component.ts:2237-2239`, `destination-wizard.component.ts:971`, `email-notification.service.ts:50,81`, `account-security.service.ts:100`*
1. Delete each of these `console.log`/`JSON.stringify` calls — none are load-bearing for functionality, they're leftover debugging output.
2. Grep the rest of the portal for similar patterns (`console.log(JSON.stringify(`, `console.log(.*[Cc]onnection`, `console.log(.*email`) to catch any not covered by this audit's sample.
3. Add an ESLint rule (`no-console`, allowing `warn`/`error` only if genuinely needed) enforced in the production build/CI to prevent regressions.
4. **Verify:** run through the Epic connector setup wizard and the mapping/destination wizard in a browser with dev tools open — confirm no connection secrets, mapping schemas, or PII appear in the console.

**17. localStorage mapping snapshots persist indefinitely** `Should` — *File: `mapping-snapshot.service.ts:6,61-68`*
1. Add an `expiresAt` timestamp when writing a snapshot, and check it on read — treat an expired entry as absent.
2. Clear all `fhirbridge.mappingSnapshot.*` keys explicitly in the logout flow (`AuthService.logout()` or equivalent).
3. As a faster interim mitigation, consider moving this from `localStorage` to `sessionStorage` so it's at least bounded to the browser session while the backend-persistence version of this feature is built.
4. **Verify:** log out, then inspect `localStorage` in dev tools and confirm no `fhirbridge.mappingSnapshot.*` keys remain.

**18. Stale password-change claim** `Should` — *Files: gate middleware in `Program.cs`, `JwtAccessTokenIssuer.cs:51`*
1. Low urgency — only address if your threat model cares about the gap between an admin's forced reset and the user's next token refresh.
2. Option A (simplest): shorten the access token lifetime so the exposure window shrinks on its own.
3. Option B (stronger): have the gate middleware check `MustChangePassword`/`MfaSetupRequired` against the database on each request instead of trusting only the JWT claim — add a short-lived cache (e.g. 30–60 seconds) in front of the DB check to avoid a full per-request query cost.
4. **Verify:** force a password reset on a user with an active session, confirm they're required to change their password well before natural token expiry.

**19. 8-hour dev token lifetime** `Should` — *File: `appsettings.Development.json`*
1. No code change needed.
2. Add a line to the deployment/runbook documentation stating the Development configuration (including its extended token lifetime) must never be used for an internet-facing environment.
3. **Verify:** confirm CI/CD deployment pipelines only ever apply `appsettings.json`/`appsettings.Production.json` to real environments, never `appsettings.Development.json`.

**20. Auth interceptor has no scheme check** `Should` — *File: `portal/src/app/auth/interceptors/auth.interceptor.ts`*
1. Add a guard at the top of the interceptor: if the outgoing request URL doesn't start with `https://` (allow `http://localhost`/`http://127.0.0.1` for local dev only), skip attaching the Authorization header and log a warning.
2. Alternatively, assert at application bootstrap that `environment.apiBaseUrl` is HTTPS in any non-local build, failing fast rather than relying on the interceptor to catch it per-request.
3. **Verify:** point a local build at an intentionally misconfigured `http://` API URL and confirm the token is not sent.

**21. Version disclosure on login page** `Optional` — *File: `portal/src/app/layout/app-footer/app-footer.component.ts`*
1. Optional, low impact. If reducing fingerprinting surface matters, wrap the precise build/version string in `*ngIf="isAuthenticated"` so it only shows inside the authenticated shell, not on the public login page.
2. Keep branding/support links on the public footer; drop just the version tag.
3. **Verify:** view page source on the logged-out login page and confirm no build/image tag is present.

### Organizational items

Each of these already has a drafted template in this folder with `[ORGANIZATION TO COMPLETE]` / `[TO ASSIGN]` markers at every point requiring a human decision or signature — engineering cannot complete these alone.

1. **Assign owners for the Security Official and Privacy Official roles.** `MUST`
   - Open `policies/02-assigned-security-responsibility.md` and fill in real names/titles — these can be the same person at a small org, but the role must be named, not left blank.
   - Get written acknowledgment (email or signature) that the named person accepts the responsibility — auditors ask for evidence of this, not just a document that names a role.
   - Nothing else on this list is real until this step is done, since every other policy references "the Security Official" as the approver/owner.

2. **Walk through and approve the 15 policy documents in `policies/` with leadership.** `MUST`
   - Assign one reviewer per document (or one reviewer for all 15, if the org is small) and set a deadline per the roadmap's 1–3 week window.
   - Replace every `[ORGANIZATION TO COMPLETE]`/`[TO ASSIGN]` placeholder with real values (names, cadences, tool names).
   - Route through leadership for formal approval (a dated sign-off, e-signature, or meeting minutes recording approval) — an unapproved draft doesn't count as an adopted policy for audit purposes.
   - Set a recurring annual review reminder for each policy (most HIPAA policies expect periodic re-review).

3. **Run the risk-assessment workshop.** `MUST`
   - Schedule a working session with engineering + security leads; use `risk-assessment/asset-inventory.md` as the agenda skeleton (walk through each asset category and confirm it's complete against the current Azure resource list).
   - Work through `risk-assessment/threat-vulnerability-register.md` entry by entry, scoring likelihood/impact for each of the 30 entries — don't accept default scores without discussion.
   - Explicitly fold in this audit's findings (§4a) as new register entries where they aren't already captured, so the risk register reflects current reality, not last quarter's.
   - Assign an owner and target date for each item in `risk-assessment/risk-treatment-plan.md`; track to closure.
   - Repeat annually, or whenever a material architecture change happens (new destination type, new source connector, new subprocessor).

4. **Get BAAs signed.** `MUST`
   - Enumerate every Azure service actually in use (App Service/Container Apps, SQL Database, Key Vault, Redis Cache, Service Bus, Blob Storage, Entra ID) and confirm each is listed on Microsoft's HIPAA-covered-services list under your Online Services Terms.
   - Execute the Microsoft/Azure BAA (via the Microsoft 365 admin center or your enterprise agreement) if not already in place.
   - For every downstream subprocessor identified in `baa/subprocessor-register.md` (e.g. SFTP hosting, any third-party email/notification provider), request and countersign their BAA — track signed date and renewal date in the register.
   - Use `baa/baa-template.md` for outbound BAAs with your own customers (the covered entities you serve); route through legal before any signature.
   - Do not send production PHI to any subprocessor whose BAA isn't yet signed — treat an unsigned BAA as a hard blocker, not a paperwork formality.

5. **Implement and test backup/DR.** `MUST`
   - Work through `backup-disaster-recovery.md` and implement each described mechanism in real infrastructure (Azure Backup vault for the database, automated SQL maintenance/backup jobs, documented RPO/RTO targets).
   - Actually run a restore test — restore a backup to a scratch environment and verify data integrity — and record the result and date; an untested backup plan doesn't satisfy the contingency-plan requirement.
   - Schedule recurring restore tests (e.g. quarterly) rather than a one-time exercise.

6. **Engage a penetration-testing firm.** `Should`
   - Use `pen-test/rfp-vendor-selection.md` to shortlist and select a firm with healthcare/HIPAA testing experience.
   - Sign `pen-test/scope-and-rules-of-engagement.md`, explicitly scoping the test to a **synthetic-data staging environment** — never point a pen test at production PHI.
   - Prioritize the test plan in `pen-test/test-plan-checklist.md` toward the areas this code audit already flagged (token handling, message queue access, de-identification bypass) so the pen test validates the fixes rather than duplicating this review from scratch.
   - Track every finding to closure in `pen-test/remediation-tracker.md`.

7. **Pursue SOC 2.** `Optional`
   - Only start this after items 1–6 are operating — a SOC 2 examination needs evidence of controls *operating*, not just designed.
   - Close the gaps identified in `soc2/readiness-assessment.md`.
   - Engage a licensed CPA firm using `soc2/auditor-rfp-and-selection.md`.
   - Complete a Type I examination first (design of controls at a point in time), then run the Type II observation window (typically 6–12 months of operating evidence) before the final Type II report.
   - Keep `soc2/evidence-matrix.md` up to date throughout the observation window — it's much easier to populate incrementally than to reconstruct evidence retroactively at audit time.

---

## 6. Re-verification

Once the technical fixes in §5 land, re-run a targeted audit against items 1–21 above and update both this document and `hipaa-soc2-control-matrix.md` to reflect the corrected status — don't let the matrix drift back out of sync with the code.
