# 22 — CDS Hooks 2.0 service (Clinical Decision Support)

> Part of the [Backend Architecture Guide](README.md). Admin how-to: [Clinical Decision Support user guide](../user-guide/clinical-decision-support.md).
> **Status: v1 built 2026-10-12 on `feature/cds-hooks`, then extended the same day with Phase 1b (Epic
> compatibility) and Phase 2 (FHIR callback, one-click fixes, per-service dismiss reasons and card links, Epic setup
> sheet). Uncommitted at the time of writing.** Phase 3 (Epic go-live readiness: readiness report, clinical sign-off,
> go-live checklist; sections 20 and 21) added 2026-10-16. Phase 4 (Da Vinci Coverage Requirements Discovery, CRD:
> payers, payer rules with file import, payer relay, systemActions; section 23) added 2026-10-16. Solution builds, architecture tests 11/11, unit tests green.
> Exercised end to end through the real DI graph with in-memory repositories and self-signed ES384 tokens; not yet
> run against a live Epic build or the live public sandbox.

## 1. Purpose

[HL7 CDS Hooks 2.0](https://cds-hooks.hl7.org/2.0/) lets an EHR call an outside service at fixed moments in the
clinician's work (chart opened, order selected, order signed) and show the advice cards it returns. Segue now acts
as a **CDS service**: EHRs call it synchronously, it answers with cards within 1–2 seconds, and it keeps **no EHR
data**. Only call metadata is recorded. When the EHR leaves out data a rule needs, Segue may read it back from the
EHR's own FHIR server with the short-lived token the EHR sent (section 10); nothing fetched is kept. Cards can carry
one-click fixes that add or remove the clinician's **draft** orders or add a problem / encounter diagnosis
(section 11); they are off by default.

## 2. Why this is not a workflow

Segue's two pipeline paths (configured pipeline and Runtime DAG) are built for moving data: they are asynchronous,
queue or schedule driven, persist run state and checkpoints, write to destinations, and take seconds to minutes.
CDS Hooks needs the opposite:

| Need | Workflow engine | CDS Hooks path |
|---|---|---|
| Latency | seconds to minutes, queued | one HTTP round trip, 1.5 s budget per evaluation |
| Data kept | run state, lineage, destination writes | nothing from the request; one metadata row per call |
| Trigger | schedule, webhook, manual | the EHR, at a hook, with a signed JWT |
| Output | records written somewhere | cards in the HTTP response |
| Auth | portal users / SMART tokens Segue holds | tokens the EHR signs, checked against registered issuers |

So CDS Hooks is a separate, small request/response path: controller → `ICdsHooksService` → `CdsEvaluator` →
handler. It shares only cross-cutting pieces (governance logger, System Settings cache, `IDistributedCache`,
observability, retention purge).

## 3. Model: handler types and configured instances

- **Handler type** (`ICdsServiceHandler`, code): the rules. Five are built in. Each is a DI singleton found by
  key through `ICdsServiceHandlerRegistry`, never through a `switch`.
- **Service instance** (`CdsServiceInstance`, data): an admin-created, named configuration of one handler type on
  one hook, with its own settings, card source and issuer allow-list. Any number of instances per type.
- **Trusted issuer** (`CdsTrustedIssuer`, data): an EHR whose signed calls Segue accepts.

| Entity | Key fields | Notes |
|---|---|---|
| `CdsTrustedIssuer` | Name, Vendor (free text), Issuer (`iss`), JwksUrl?, JwksJson?, Audience?, IsEnabled, **ExpectedSubject?** (`sub`, ≤500), **FhirBaseUrl?** (https, ≤1000, trailing `/` trimmed), **AllowCallback** (default true) | `AuditableEntity<Guid>`, soft delete, RowVersion. Issuer unique among non-deleted rows. JwksUrl or JwksJson required. `EffectiveFhirBaseUrl` = FhirBaseUrl, else Issuer (Epic's `iss` is its FHIR base). |
| `CdsServiceInstance` | ServiceId (URL slug), Title, HandlerKey, Hook, Description, IsEnabled, SettingsJson, CardSourceLabel, CardSourceUrl?, AllowedIssuerIds, **FetchMissingData** (default true), **FixesJson?**, **OverrideReasonsJson?**, **LinksJson?**, *SignedOffBy?* (≤200), *SignedOffAtUtc?*, *SignOffNote?* (≤500) | Same base. ServiceId `^[a-z0-9]+(?:-[a-z0-9]+)*$`, 3–100 chars, unique among non-deleted rows. Empty allow-list = any enabled trusted issuer. The three JSON columns hold the Domain value objects in `ValueObjects/CdsServiceOptions.cs`; null means fixes off / default reasons / no links. |
| `CdsCallLog` (Governance) | OccurredOnUtc, Kind, ServiceId, ServiceInstanceId?, HandlerKey, Hook, HookInstance, Issuer, TrustedIssuerId?, Outcome, HttpStatus, CardCount, DurationMs, Detail, **TriggerAction?** (20), **EpicVersion?** (50), **CriteriaId?** (100), **ImplementationVersion?** (50), **CallbackResourceCount?**, **CallbackMs?**, **SuggestionsOffered**, **SuggestionsAccepted**, **OverrideReasonCodes?** (300) | Append-only, metadata only. Purged by the retention job under data class `CdsCallLog`. New values are written through `CdsCallLogExtras`. |

Enums: `CdsCallKind` (Invoke, Feedback) and `CdsCallOutcome` (Success, NoCards, Degraded, PreconditionFailed,
Unauthorized, Forbidden, NotFound, BadRequest, Error).

Migrations:

| Migration | SQL Server | PostgreSQL | Adds |
|---|---|---|---|
| `AddCdsHooks` | `20261012053425` | `20261012053457` | The three tables. Unique indexes filtered to non-deleted rows; the PostgreSQL filter syntax is overridden in `FHIRBridgeDbContext`. |
| `AddCdsEpicAndFixes` | `20261012090809` | `20261012090906` | The columns in bold above. `AllowCallback` and `FetchMissingData` are added with default `true` for existing rows (the model has no store default, so EF never mistakes an explicit `false` for "unset"); the counters default to 0; everything else is nullable. |
| `AddCdsClinicalSignOff` | `20261016052653` | `20261016052726` | The three nullable sign-off columns in italics above (section 20). Existing services start "not clinically reviewed". |
| `AddCdsCoverageRequirements` | `20261016062127` | `20261016062144` | Tables `CdsPayers` and `CdsCoverageRules`, and the coverage requirements columns of `CdsCallLogs` (section 23). `SystemActionCount` defaults to 0; the rest are nullable. |

None of these migrations has been applied to any shared database.

### Where the code lives

| Layer | Files |
|---|---|
| Domain | `Entities/CdsTrustedIssuer.cs`, `Entities/CdsServiceInstance.cs`, `Entities/Governance/CdsCallLog.cs`, `Enums/CdsCall*.cs`, `ValueObjects/CdsServiceOptions.cs` (fixes, dismiss reasons, links and their shape rules) |
| Application | `Services/Cds/CdsReadinessRules.cs` + `DTOs/Cds/CdsReadinessDtos.cs` (go-live readiness, section 20), `Abstractions/Cds/*` (handler interface, registry, settings values, `CdsHandlerContext` with the suggestion builders and `CdsSuggestionRules`, `CdsFixTypeCatalog`, `ICdsFhirCallbackClient`, `ICdsHooksService`, `ICdsTestService`, base-URL resolver), `DTOs/Cds/CdsHooksWireModels.cs` (spec JSON), `DTOs/Cds/CdsEpicWireSupport.cs` (Epic extension keys, fix code systems, `CdsRequestMetadata`, `CdsTopic`), `DTOs/Cds/CdsAdminDtos.cs`, `DTOs/Cds/CdsSetupSheetDtos.cs`, `Services/Cds/*` (admin services, registry, resolver, `CdsSetupSheetService`, `CdsCallbackTestService`) |
| Infrastructure | `Cds/CdsReadinessService.cs`, `Cds/CdsHooksService.cs`, `CdsEvaluator.cs`, `CdsJwtValidator.cs`, `CdsJwksProvider.cs`, `CdsJwksHttpHandler.cs` (address guard, shared with the callback), `CdsFhirCallbackClient.cs`, `CdsTestService.cs`, `CdsOptions.cs`, `Cds/Handlers/*`, EF / in-memory repositories, `Persistence/Configurations/CdsConfigurations.cs` |
| Api | `Controllers/CdsHooksController.cs` (public), `Controllers/V1/Cds*Controller.cs` (admin), `Security/CdsHooksAuthenticationHandler.cs`, CORS policy in `Cors/DynamicPortalCorsPolicyProvider.cs`, rate policy in `Program.cs` |
| Observability | `SharedKernel/Observability/ICdsMetrics.cs`, `Observability/FhirBridgeCdsMetrics.cs` |
| Portal | `portal/src/app/clinical-decision-support/` |

## 4. Public endpoints (outside `/api/v1`)

All three live on `CdsHooksController` at the spec's fixed path `/cds-services`.

| Method and path | Auth | Answers |
|---|---|---|
| `GET /cds-services` | none | Discovery: every **enabled** instance whose handler type is registered, ordered by service id, with `hook`, `title`, `description`, `id` and `prefetch` templates. |
| `POST /cds-services/{id}` | EHR-signed JWT | `200 { cards: [...] }` (possibly empty), or `400 / 401 / 403 / 404 / 412 / 500` with `{ error }`. 401 also sets `WWW-Authenticate: Bearer`. |
| `POST /cds-services/{id}/feedback` | EHR-signed JWT | `200` (empty body) or an error as above. Spec 2.0 feedback: each item needs `card` (uuid) and `outcome` `accepted` or `overridden`. Batched items (Epic) are accepted; see section 9 for Epic's override-reason shape. |

Order of checks for invoke and feedback (`CdsHooksService.AdmitAsync`, then `CdsEvaluator`):

1. The service id is normalised; an unknown or **disabled** instance gives 404.
2. The JWT is validated (section 5). Failure gives 401 and a governance security event `CdsCallRejected`.
3. The issuer must be on the instance's allow-list (if it has one). Otherwise 403 and a security event.
4. Invoke only: the handler type must still be registered (else 500).
5. Invoke only: request checks. Missing body, hook, `hookInstance` or `context` gives 400. A hook different from
   the instance's hook gives 400.
6. Prefetch rule (section 7), then the handler runs inside the time budget.

Body limits: 4 MB for invoke, 256 KB for feedback. `[ApiController]`, `[FromBody]` and `[Consumes]` are
deliberately **not** used: the controller reads the body as JSON itself (web defaults, any Content-Type), so a
body that cannot be read still reaches the service and is answered (and logged) as a 400 "not valid JSON" after
the id and JWT checks, instead of an un-audited automatic 400 or 415. Model validation is not applied, so an odd
value in a member v1 never reads (e.g. `fhirAuthorization.expires_in` sent as a string) does not reject the call.

**License gate.** The CDS endpoints are a licensed feature even though they sit outside `/api/v1`. While the
license is not Active, the license-gate middleware in `Program.cs` answers `GET` and `POST` under
`/cds-services` with `503 { error }`; invoke and feedback calls still get a call log row (outcome `Error`,
HTTP 503, Detail "Refused: Segue has no active license.") via `ICdsHooksService.RecordUnlicensedAsync`.

A client that hangs up (or a request that is aborted) mid-call still leaves its row: outcome `Error`, HTTP 499.
Each call writes at most one row.

## 5. Authentication: the EHR's JWT

### Scheme wiring

`FhirBridgeAuthenticationExtensions` forwards every request under `/cds-services` to a `CdsHooks` scheme
(`CdsHooksAuthenticationHandler`) that always returns `NoResult`. This keeps EHR tokens away from the Local and
Entra JWT handlers (which would reject and log them) and from any portal session cookie. The controller is
`[AllowAnonymous]`; the real check is done per call by `CdsJwtValidator`, because the expected audience depends
on the exact URL called, the allow-list is per instance, and every rejection must reach the CDS call log.

### `CdsJwtValidator` rules

| Check | Rule |
|---|---|
| Token | `Authorization: Bearer <jwt>` required. |
| `iss` | Must equal the `Issuer` of an **enabled** `CdsTrustedIssuer` (exact match). |
| Algorithm | `RS256`, `RS384`, `ES256` or `ES384` only. Anything else (including `none`, HS*) is refused before key lookup. |
| `jku` (header) | Optional. When present it must equal the issuer's configured `JwksUrl` after `CdsTrustedIssuer.NormalizeUrl` (scheme and host lower-cased, default port dropped, trailing `/` dropped, path case kept). Checked **before** any key lookup. A different jku, or a jku on an issuer that has only pasted keys, is refused (401). Keys are **never** fetched from a token's jku. Epic always sends one. |
| Signature | Keys from the pasted `JwksJson` plus the keys fetched from `JwksUrl` (both used when both are set). |
| `aud` | Must equal the service URL called (`{base}/cds-services/{id}`, `{base}` = the **configured** `Cds:PublicBaseUrl`); feedback also accepts `{serviceUrl}/feedback`. Compared case-insensitively, trailing slash ignored. If the issuer has an `Audience` override, only that value is accepted. With no `Cds:PublicBaseUrl` and no override the call is refused (401): the audience is never derived from the request's Host header. |
| `exp` | Required. 60 s clock skew. A token whose `exp` is more than 60 minutes away is refused. |
| `nbf` | Checked when present (Epic sets it about 5 minutes before `iat`). |
| `sub` | Checked only when the issuer has an `ExpectedSubject`: must be equal (ordinal). Epic's `sub` is the health system's CDS client id. Checked after the signature, before the `jti` replay check. |
| `jti` | Required, accepted **once** per issuer, by an atomic set-if-absent in `CdsReplayGuard` (key `cds:jti:` + SHA-256(`iss` + `jti`), kept until `exp` + skew): Redis `SET NX` when `ConnectionStrings:Redis` is set, otherwise a serialized check on the in-memory `IDistributedCache`. Two concurrent requests with one token: only one is accepted. **A multi-node deployment needs Redis for replay protection**; the in-memory fallback covers one node only. |

Failure reasons are short, non-PHI sentences (for example "The token's aud (...) does not match the expected ...
Check Cds:PublicBaseUrl or the issuer's audience override.") and go to the call log `Detail` and the security
event. The token itself is never logged. The jku and sub failure messages do not echo the values sent.

Do not confuse this JWT with `fhirAuthorization.access_token` in the request body: that is the EHR's bearer token
for its own FHIR server, used only for the callback (section 10).

### JWKS fetching (`CdsJwksProvider`)

- **https only.** A JWKS URL must be an absolute `https` URL (domain rule on `CdsTrustedIssuer`, portal
  validator, and re-checked by the provider for rows saved earlier): keys fetched in clear text could be swapped.
- Named `HttpClient` `CdsJwks`; each fetch is cut off after 5 s (`Cds:JwksFetchTimeoutSeconds`). Its primary
  handler (`CdsJwksHttpHandler`) resolves the host itself and refuses loopback, private, carrier-grade NAT,
  link-local (incl. `169.254.169.254`) and other non-public addresses, and follows no redirects, so an
  admin-entered URL cannot reach internal endpoints. `Cds:AllowPrivateJwksAddresses` (default false) lifts this
  for an EHR on the same private network.
- Responses over 256 KB or with no usable signing key are ignored.
- Keys are cached in `IMemoryCache` for 60 minutes (`Cds:JwksCacheMinutes`), per URL.
- A token with a `kid` not in the cached set triggers one refetch (key rotation), at most once per 30 s per URL.
  The 30 s cooldown counts every **attempt**, not only successful fetches.
- A failed fetch is cached too: the last good key set is kept (with a fresh attempt time), or, with none, an
  empty set for 30 s. Concurrent misses for one URL share a single fetch. Key lookup happens before the signature
  is verified, so this is what keeps unauthenticated callers from turning a down JWKS endpoint into a stream of
  outbound fetches and slow calls.

### Public base URL

**`Cds:PublicBaseUrl` is required to accept CDS calls.** The expected `aud` is built only from it (System
Settings value, then appsettings / environment), never from the request's Host or forwarded headers: otherwise a
token a trusted EHR signed for another CDS service could be replayed to Segue with a matching `Host`. Without it,
invoke and feedback answer 401 (Detail names the setting) unless the issuer has an audience override.
`appsettings.Development.json` sets `http://localhost:5000`; the Azure template sets
`Cds__PublicBaseUrl=https://<custom domain>`. The admin screens (`GET /cds/discovery-info`) still fall back to the
request origin for display, and show a warning that the setting is missing.

## 6. CORS, CSRF and rate limiting

- **CORS:** policy `CdsHooks` in `DynamicPortalCorsPolicyProvider`, applied with `[EnableCors]` on the controller.
  Any origin; methods GET, POST, OPTIONS; headers `Authorization`, `Content-Type`, `Accept`,
  `X-Requested-With`; **never credentials**. Needed because browser-based CDS clients (the public sandbox) call
  directly from the page. The Portal policy is unchanged.
- **CSRF:** the controller is `[CsrfExempt]`. No portal cookie is involved and the CORS policy allows no
  credentials, so there is nothing for CSRF to protect; the JWT is the credential. Covered by
  `CsrfExemptionTests` in the architecture tests.
- **Rate limit:** `[EnableRateLimiting("cds")]`, a fixed window per client IP. `RateLimiting:Cds:PermitPerWindow`
  (default 600) and `RateLimiting:Cds:WindowMinutes` (default 1), each read from System Settings, then
  configuration. They are read **once at startup**, so a change needs an API restart. Over the limit: 429.

## 7. Evaluation (`CdsEvaluator`)

One path serves both the live endpoint and the admin Test screen.

1. Settings: `CdsSettingsValues.Resolve` merges the instance's `SettingsJson` with the handler's defaults.
2. Request checks (section 4, step 5).
3. **Prefetch rule (spec 2.0).** The required keys are the handler's prefetch templates for the hook.
   - a key is **absent** and the request has no `fhirServer` → **412**, outcome `PreconditionFailed` (unchanged
     from v1);
   - a key is absent and the request has a `fhirServer` → the **FHIR callback** tries to fetch it (section 10),
     inside the time budget. Fetched resources are merged into `request.Prefetch` without overwriting keys the
     client sent;
   - a key still absent after that, or present but `null` (the client could not fetch it) → **200 with no cards**,
     outcome `Degraded`. The Detail names the keys and adds a short non-PHI note in brackets: why the callback did
     not run ("Fetch missing data is off for this service", "callbacks are off for this trusted issuer",
     "fhirServer is not the trusted issuer's FHIR base URL, so it was not called", "missing data is not fetched
     without an authenticated EHR caller" on admin Test runs) or the callback's failure reason ("callback: timed
     out", "callback: HTTP 401", ...).
4. **Time budget:** `Cds:TimeoutMs` (System Settings override, then config, default 1500, clamped to
   100–30 000 ms). It covers the callback and the handler. The handler runs off the request thread; running out of
   time → 200 with no cards, `Degraded`. If the callback used the whole budget the handler is not started and the
   Detail says so ("Fetching missing data used the whole N ms time budget"); a callback that ran is always recorded
   (`callbackResourceCount`, 0 when cut off, and `callbackMs`). A handler exception → 500, `Error` (only the
   exception type is logged).
5. **Card clean-up (`Sanitize`):** cards with no summary are dropped; summary cut to under 140 characters (139,
   ending in "…", never splitting a surrogate pair); indicator forced to `info | warning | critical` (else
   `info`); a uuid is added if missing; `source.label` / `source.url` always come from the instance
   (`CardSourceLabel`, `CardSourceUrl`, the handler's `source.topic` is kept); at most 25 cards.
   **Links** are kept only when they have a label, an absolute **https** URL (≤ 2048), type `absolute` or `smart`
   and an `appContext` of at most 4096 characters; `source.icon` only when it is an absolute https URL.
   **Suggestions:** all are dropped when the instance's fixes are off; otherwise each must pass
   `CdsSuggestionRules` (section 11) and at most 10 are kept per card. `selectionBehavior` is set to `"any"`
   exactly when suggestions remain. Only the **count** of dropped suggestions is logged.
6. Outcome: `Success` (cards), `NoCards`, or `Degraded` when the handler returned a `DegradedReason`.

Cards carry: summary, detail (markdown), indicator, source (label, url, static `topic`), links (the handler's own
plus the instance's, section 12), uuid, `overrideReasons` (the instance's list, section 12), and, when fixes are on,
`suggestions` + `selectionBehavior: "any"`.

**systemActions** are sent **only** by a handler whose `ReturnsSystemActions` is true (today only
`coverage-requirements`, section 23); the evaluator drops every systemAction of any other handler (count logged
only). Each kept action must be `type: "update"` of a resource of this request (its draft / dispatched orders,
appointments or encounter, matched by `Type/id`), must carry the CRD coverage-information extension, and **apart
from its coverage-information extensions it must equal the request's own copy of that resource** (a systemAction
may add coverage information, never change the order: code, status, quantity, other extensions); at most 25
are sent. A handler may also set `CdsHandlerResult.SourceLabel` to replace the instance's card `source.label` (the
CRD relay uses "&lt;payer&gt; via Segue").

**Preparers.** A handler type may have an `ICdsHandlerPreparer` (found by handler key in a dictionary, never a
switch). It runs after the prefetch rule and callback, inside the same time budget, and does the I/O the pure
handler may not (the coverage engine: payer rules, payer relay). Its `Data` reaches the handler as
`CdsHandlerContext.Prepared`; its `Note` becomes the call's Detail on success; its coverage facts go to the call
log. A preparer that throws or runs out of time gives 200 with no cards, `Degraded`.

## 8. Handler registry and the built-in handlers

`CdsServiceHandlerRegistry` collects every `ICdsServiceHandler` from DI, rejects duplicate keys at startup and
exposes `All` (ordered by display name) and `Find(key)`. Discovery, the admin catalog, validation and evaluation
all go through it.

| Key | Display name | Hooks | Prefetch keys | Settings (defaults) | Sample request triggers |
|---|---|---|---|---|---|
| `drug-allergy` | Drug–allergy check | order-select, order-sign | `allergies` | `allergyIndicator` (warning), `highCriticalityIndicator` (critical), `checkCrossReactivity` (true), `crossReactivityIndicator` (info), `includeInactiveAllergies` (false) | critical: penicillin allergy vs amoxicillin |
| `drug-interaction` | Drug–drug interaction check | order-select, order-sign | `medications` (active MedicationRequests) | `minimumSeverity` (moderate), `checkWithinDraftOrders` (true), `contraindicatedIndicator` (critical), `majorIndicator` (warning), `moderateIndicator` (info) | warning: warfarin with ibuprofen |
| `renal-dose` | Renal dose check | order-select, order-sign | `egfr` (latest eGFR Observation) | `egfrThreshold` (60), `useDrugThresholds` (true), `maxResultAgeDays` (365), `indicator` (warning), `avoidIndicator` (critical) | critical: metformin at eGFR 25 |
| `care-gap` | Care-gap reminders | patient-view | `patient`, `conditions`, `hba1c`, `immunizations`, `procedures` | `checkHba1c` (true), `hba1cIntervalMonths` (6), `checkInfluenza` (true), `influenzaIntervalMonths` (12), `checkColorectalScreening` (true), `colorectalIntervalYears` (10), `colorectalAgeFrom` (45), `colorectalAgeTo` (75), `indicator` (info) | 3 info cards (HbA1c, influenza, colorectal screening) |
| `prior-auth` | Prior-authorization reminder | order-sign | none (context only) | `codes` (list, sample CPT codes), `payerName` (""), `submissionUrl` (""), `indicator` (warning) | warning: CPT 70553 (MRI brain) |
| `coverage-requirements` | Coverage requirements (CRD) | order-sign, order-select, appointment-book, encounter-start, encounter-discharge | `coverage` (all hooks), `encounter` (encounter hooks) | see section 23 | systemAction + card once a payer with identifier `EXAMPLE-PAYER` (or name "Example Health Plan") has a rule for CPT 70553 |

Prefetch templates use `{{context.patientId}}`, for example `AllergyIntolerance?patient={{context.patientId}}`.
The same templates are the handlers' **fallback queries** for the callback (`GetFallbackQueries`, defaulting to
`GetPrefetchTemplates` in the base class). On `order-select`, the order checks look only at `context.selections`
when given (the newly selected orders, e.g. `"MedicationRequest/<id>"`); otherwise at every order in
`context.draftOrders`.

All five derive from `CdsServiceHandlerBase`, which guarantees a handler **never throws on bad input**: anything
it cannot read becomes no cards plus a short degraded reason. `CdsFhirReader` reads R4 and R5 shapes leniently
(and Epic's shapes, section 9); `CdsDrugMatcher` maps a medication concept to ingredients and classes, by code
(RxNorm by URL or by OID `urn:oid:2.16.840.1.113883.6.88`) or, failing that, by matching the display text.

Every card carries one **static topic code** per rule in `source.topic` (system `urn:segue:cds:topic`, built
with `CdsTopic.Create`, letters and digits only, ≤ 100 characters):

| Handler | Topic codes |
|---|---|
| `drug-allergy` | `DrugAllergyMatch`, `DrugAllergyCrossReactivity` |
| `drug-interaction` | `DrugInteractionContraindicated`, `DrugInteractionMajor`, `DrugInteractionModerate` (`DrugInteraction` as fallback) |
| `renal-dose` | `RenalDoseReview`, `RenalDoseAvoid` |
| `care-gap` | `CareGapHba1c`, `CareGapInfluenza`, `CareGapColorectal` |
| `prior-auth` | `PriorAuthRequired` |
| `coverage-requirements` | `CoverageInformation`, `CoveragePolicy`, `CoverageDocumentation`, `CoverageUnknown` (or the CRD card type `coverage-info`, setting `topicCodes = crd`) |

Epic ties a clinician's override to the topic code, so **never change an existing code**; add a new one for a new
rule.

**Sample requests** (`BuildSampleRequest`) are in Epic's shape with clearly made-up values: `userId`
`PractitionerRole/...`, Epic request extensions (trigger action "5" for patient-view and "18" for order hooks:
illustrative, not an Epic mapping), `draftOrders` as a `collection` Bundle with `fullUrl`s, a draft
MedicationRequest with `medicationReference: "Medication/med-<id>"` plus an upper-case display and the Medication as
its own Bundle entry, and active MedicationRequests in prefetch with a contained `#med-<id>` Medication.

### Reference data is SAMPLE

All code lists, drug classes, interaction pairs, renal thresholds and prior-auth codes are in
`CdsSampleReferenceData`. They are small and illustrative, **not** a drug knowledge base. Every card's detail ends
with: *"SAMPLE rule shipped with Segue for demonstration. Requires clinical review before clinical use."* The
portal shows the same warning on the service setup page.

### Settings descriptor

Each handler publishes `IReadOnlyList<CdsSettingDescriptor>`:

```csharp
public sealed record CdsSettingDescriptor(
    string Key, string Label, string Type, JsonNode? DefaultValue,
    string? Help = null, IReadOnlyList<CdsSettingOption>? Options = null,
    bool Required = false, double? Min = null, double? Max = null);
```

`Type` is one of `number | text | select | boolean | list`. The portal builds the settings form from it, and the
admin service validates saves against it: unknown key → 400; number within `Min`/`Max`; select value must be in
`Options`; list is trimmed with blanks and duplicates dropped; null or missing uses the default; required with no
default → 400. Handlers read values with `context.Settings.GetNumber / GetText / GetBoolean / ...`.

### How to add a handler type

1. Add a class in `src/FHIRBridge.Infrastructure/Cds/Handlers/` deriving from `CdsServiceHandlerBase`
   (or implementing `ICdsServiceHandler` directly). Add its key to `CdsHandlerKeys` and any new hook to
   `CdsHookNames`.
2. Implement `Key`, `DisplayName`, `Description`, `SupportedHooks`, `Settings`, `GetPrefetchTemplates(hook)`
   (every key you return is treated as **required**), `BuildSampleRequest(hook)` (made-up data in Epic's shape that
   produces at least one card with default settings) and `Evaluate(context, token)`. Optionally override
   `GetFallbackQueries(hook)` (callback queries; defaults to the prefetch templates) and `SupportedFixTypes` (build
   descriptors with `CdsFixTypeCatalog`; default none). `UsesSampleReferenceData` is `true` in
   `CdsServiceHandlerBase` (every built-in rule set reads `CdsSampleReferenceData`); override it with `false` only
   for rules backed by reviewed reference data, so the readiness report stops asking for a sample-data sign-off.
3. Build cards with `context.CreateCard(...)`, passing a static `CdsTopic.Create("YourRuleCode")`,
   `context.OverrideReasons`, the instance's `context.Links` (after your own links) and any suggestions from
   `context.CreateDeleteSuggestion` / `CreateCreateSuggestion` (they return null when the fix is off; resources come
   from `CdsFixResources`). End detail text with `Detail(...)` so the SAMPLE notice is added if your rules use
   sample data. Check the cancellation token in long loops.
4. Do no I/O in a handler: no EF, persistence, `HttpClient` or logging (`CdsHooksArchitectureTests`). Data a rule
   needs is declared as prefetch / fallback queries; the evaluator and callback client fetch it.
5. Register it in `FHIRBridge.Infrastructure/DependencyInjection.cs`:
   `services.AddSingleton<ICdsServiceHandler, MyHandler>();`. Nothing else changes: discovery, the catalog,
   the portal picker, the settings form, the fixes editor and the setup sheet pick it up.
6. Never log or keep anything from `context.Request`; return reasons as short non-PHI sentences.

Removing a handler type leaves its instances in the database: they drop out of discovery, invoke returns 500,
and the portal marks them "(unavailable)".

## 9. Epic compatibility

Taken from Epic's CDS Hooks tutorial on fhir.epic.com; treat these as requirements.

### What Epic sends

- **Hooks:** `patient-view`, `order-select`, `order-sign` only (the three Segue supports).
- **Discovery is not read.** The health system configures each service by hand: endpoint, hook, prefetch, override
  reason mapping. Segue's **Epic setup sheet** (section 13) lists every value they need.
- **`fhirServer` + `fhirAuthorization` are always sent** (short-lived bearer token). This is what the callback uses.
- **Inbound JWT:** header `alg` (default RS384), `jku` (JWK Set URL), `kid`, `typ: JWT`; payload `aud` (the service
  endpoint, entered manually by the health system), `iss` (the organisation's FHIR base, e.g.
  `https://host/api/FHIR/R4`), `sub` (CDS client id), `exp`, `iat`, `nbf` (≈5 min before `iat`), `jti`. Segue
  enforces the jku allow-list and optional `sub` check (section 5).
- **context:** `patientId`, `userId` (`PractitionerRole/<id>` or `Practitioner/<id>`), `encounterId` when in an
  encounter; for order hooks `draftOrders` (Bundle, type `collection`) with MedicationRequest / ServiceRequest /
  (STU3) ProcedureRequest; on `order-select`, `selections[]` = the newly selected ids.
- **Medications by reference.** Since Feb 2024 Epic sends `medicationReference {reference: "Medication/<id>",
  display: "DEXTROMETHORPHAN HBR 15 MG/5ML PO SYRP"}` with the Medication as a contained resource or a Bundle entry,
  and no `medicationCodeableConcept`.
- **Request extensions** (`extension` on the request): `com.epic.cdshooks.request.bpa-trigger-action` (e.g. 5, 6,
  7, 18, 23, 26, 27, 29, 60), `.cds-hooks-specification-version`, `.fhir-version`, `.criteria-id`, `.epic-version`,
  `.cds-hooks-implementation-version`, `.cds-hooks-su-version`.

### How Segue reads it

| Area | Behaviour |
|---|---|
| Medication concept | `CdsFhirReader.MedicationConcept` / `OrderConcept` take a `CdsMedicationIndex` and resolve `medicationReference` (and R5 `medication.reference`) in order: contained Medication named by `#id`; a Medication in the index by `Medication/id`, its `fullUrl`, or the `Medication/id` at the end of an absolute URL; a contained Medication with the same id; the only contained Medication. The concept is that Medication's code plus the reference display. If none is found, the display text alone is used and `CdsDrugMatcher` matches it by name. `CdsServiceHandlerBase.Medications(context)` indexes Medications from `draftOrders` and every prefetch value. |
| Draft orders | `DraftOrders()` keeps order types only (`*Request` including STU3 `ProcedureRequest`, plus NutritionOrder, VisionPrescription, MedicationOrder), so the Medication entries Epic adds to `draftOrders` are not checked as orders. ProcedureRequest is treated like ServiceRequest. |
| Selections | On `order-select` only the orders named in `selections` are checked. |
| Request metadata | `CdsRequestMetadata.Read` keeps trigger action (≤20), Epic version (≤50), criteria id (≤100), implementation version (≤50) — and reads spec version, FHIR version and SU version — **only when the value is a short token** (letters, digits, `.`, `-`, `_`; strings or numbers). Anything else is dropped, not stored. The first four go to the call log. |
| Feedback | Both `overrideReason` (spec 2.0, singular) and `overrideReasons` (Epic, plural, same shape) are accepted, singular first. The recorded code is `reason.code`, else Epic's `com.epic.cdshooks.feedback.overrideReason.reasonCategory` extension (e.g. `"45"`); it is kept only as a short token (≤ 40 chars). `reasonDisplay`, `reasonTitle`, `reason.display` and `userComment` are never read into logs or storage. `acceptedSuggestions[].id` are counted into `SuggestionsAccepted`. Batched items are counted one by one. Distinct codes are stored comma-separated in `OverrideReasonCodes`, cut at whole codes to fit 300 characters. |

### Epic card and suggestion constraints Segue follows

- Cards need a `uuid` to receive feedback (always set). `source.topic.code` alphanumeric, ≤ 100, static per topic
  (section 8). Detail is markdown (Epic's default); Segue sends no
  `com.epic.cdshooks.card.detail.content-type` extension. Indicator `info | warning | critical`.
- `overrideReasons` on a card: the health system maps each code to an Epic override reason; any non-empty system
  works (Segue uses `urn:segue:cds:override-reason`).
- Suggestions: `label` required (Epic doesn't show it), `uuid` for feedback, `isRecommended` pre-selects, **one
  action per suggestion**, action types **create** or **delete** only, `selectionBehavior` only `"any"`.
- Epic accepts: Condition create (Problems, Encounter Diagnosis); MedicationRequest create/delete (single order);
  ServiceRequest create/delete (single order); ServiceRequest create for order sets. **Epic ignores the order
  details in a created order and uses its own defaults.** Created resources use `status: "draft"`,
  `intent: "proposal"`, and a code from Epic's systems (section 11). A delete names the draft order by `resourceId`
  (`"MedicationRequest/<id>"`).
- Links: `label`, `url`, `type` `absolute | smart`, `appContext` (smart only), `autolaunchable` (Epic Aug 2023+).
  SMART links must be on the health system's allow-list.
- Feedback goes to `<service endpoint>/feedback`, immediately or batched.

## 10. FHIR callback (fetch missing data)

`ICdsFhirCallbackClient` (Application) / `CdsFhirCallbackClient` (Infrastructure, typed `HttpClient`). The
evaluator calls it for absent prefetch keys only. It runs only when **all** of these hold, else the note in
section 7 explains why:

1. the call was authenticated (a `CdsTrustedIssuer` is known; admin Test runs have none, so they never call back);
2. the service's `FetchMissingData` is on (default on);
3. the issuer's `AllowCallback` is on (default on);
4. `fhirServer` equals the issuer's `EffectiveFhirBaseUrl` after `NormalizeUrl` (`FhirBaseUrl`, else `Issuer`,
   since Epic's `iss` is its FHIR base);
5. the handler has a fallback query for the key and its `{{context.*}}` tokens resolve (`CdsPrefetchTemplate.Resolve`).

Rules inside the client:

- **Only the request's own `fhirServer` and `fhirAuthorization.access_token`** are used. The token is sent as
  `Authorization: Bearer`, never logged, stored or traced; a token with control or whitespace characters is refused.
  The `fhirServer` match is checked again in the client (second line of defence).
- **https only** unless `Cds:AllowInsecureCallback` (dev only; the token would travel in clear). The issuer's
  `FhirBaseUrl` may be stored as http(s) (no query or fragment), so a dev FHIR server can be named; the scheme is
  enforced here, in the client.
- **Address guard:** `CdsJwksHttpHandler.CreateGuarded(...)` — the same handler as JWKS — resolves the host itself,
  refuses private, loopback, link-local and other non-public addresses unless
  `Cds:AllowPrivateCallbackAddresses`, and follows no redirects. IPv6 forms that embed an IPv4 address (NAT64
  `64:ff9b::/96`, `64:ff9b:1::/48`, 6to4 `2002::/16`, IPv4-compatible `::/96`) are judged by that IPv4 address. It
  **never uses a proxy** (`UseProxy = false`, `HTTP(S)_PROXY` ignored): through a proxy the guard would check the
  proxy's address, not the target's.
- **Queries must stay under the FHIR base:** no absolute, rooted (`/...`), `..` or backslash paths.
- **Parallel and bounded:** all keys at once within `Cds:CallbackTimeoutMs` (System Settings, then config; default
  900; clamped to 50 … `TimeoutMs` − 100), inside the overall `Cds:TimeoutMs` budget.
- **Responses:** capped at `Cds:CallbackMaxResponseBytes` (4 MB); must be FHIR JSON with a `resourceType`; an
  OperationOutcome is not accepted.
- **Never throws on a remote failure.** The key stays null and the result carries short fixed reasons such as
  "timed out", "HTTP 401", "unreachable", "response too large", "not a FHIR resource". Unexpected exceptions are
  caught in the evaluator (type only logged) → Degraded.
- **No logging of URLs.** The typed client is registered with `.RemoveAllLoggers()` (default HttpClient logs would
  write URLs holding patient ids), and requests are flagged with `FhirBridgeCdsMetrics.UntracedRequest` so the
  OpenTelemetry HttpClient instrumentation in `ObservabilityServiceCollectionExtensions` skips them. The
  `cds.callback` span records counts only (`cds.callback.query_count`, `cds.callback.resource_count`).
- The call log records `CallbackResourceCount` and `CallbackMs` (null when no callback ran).

**Test callback** (`ICdsCallbackTestService`, `POST /api/v1/cds/trusted-issuers/{id}/test-callback`, permission
`cdshooks.test`): `GET {EffectiveFhirBaseUrl}/metadata` with **no token and no patient data**, 10 s timeout, same
address guard and https rule. Returns `CdsCallbackTestResultDto` (reachable, HTTP status, FHIR version and software
from the CapabilityStatement, duration, one-sentence message that also notes when callbacks are off for the issuer).
Unknown id → 404.

## 11. One-click fixes (suggestions)

Off by default. When on, cards carry CDS Hooks `suggestions` that the EHR turns into changes to the clinician's
**unsigned** work: remove a draft order, add a draft order, or add a problem / encounter diagnosis. The clinician
still signs.

### Wire model

`CdsSuggestion { label, uuid, isRecommended?, actions[] }`, `CdsAction { type: create|delete, description,
resource?, resourceId? }`. Card `selectionBehavior: "any"` when suggestions are present. No `systemActions`.

### Fix types (`CdsFixTypeKeys`, descriptors from `CdsFixTypeCatalog`)

| Key | Action | Resource | Needs a code | Code systems offered |
|---|---|---|---|---|
| `remove-draft-order` | delete | MedicationRequest (by `resourceId`) | no | – |
| `add-alternative-medication` | create | MedicationRequest | yes (code + drug name) | Epic preference list item, RxNorm (`urn:oid:2.16.840.1.113883.6.88`), NDC (`urn:oid:2.16.840.1.113883.6.69`) |
| `add-test-order` | create | ServiceRequest | yes (code; name optional) | Epic preference list item, Epic order set item |
| `add-problem` | create | Condition, category `problem-list-item` | yes (code + name) | ICD-10-CM (`urn:oid:2.16.840.1.113883.6.90`), Epic CMS-HCC, Epic HHS-HCC |
| `add-encounter-diagnosis` | create | Condition, category `encounter-diagnosis` | yes (code + name) | as above; offered only when the request has `encounterId` |

Epic system URIs: `urn:com.epic.cdshooks.action.code.system.preference-list-item`,
`...orderset-item`, `...cms-hcc`, `...hhs-hcc` (constants in `CdsFixCodeSystems`).

| Handler | Fix types | Mappings keyed by ("Applies to") |
|---|---|---|
| `drug-allergy` | remove draft, alternative, problem, encounter diagnosis | drug class |
| `drug-interaction` | remove draft (the ordered drug, and the other drug too when it is also a draft), alternative, problem, encounter diagnosis | ingredient (alternative); any (diagnosis) |
| `renal-dose` | remove draft, alternative, problem, encounter diagnosis | ingredient (alternative); any (diagnosis) |
| `care-gap` | test order, problem, encounter diagnosis | care gap (`hba1c`, `influenza`, `colorectal`) |
| `prior-auth` | problem, encounter diagnosis | any |

A mapping with no trigger is the fallback; a mapping for the exact trigger wins
(`CdsHandlerContext.FindFixMapping`). A fix is offered only when the master switch is on, its type is on, and (for
create fixes) a mapping applies. `isRecommended: true` goes on the first suggestion of `RecommendedFixType` on each
card only (Epic pre-selects every recommended suggestion): `CdsHandlerContext.CreateCard` and the evaluator's
sanitizer keep at most one per card, and a delete of a draft order an earlier card of the same response already
recommends is not recommended again. On a drug–drug card between two drafts, "remove the other draft" is never
recommended. `FixesEnabled` (the "Changes EHR drafts" badge, the setup sheet, the sanitizer) is true only when the
master switch is on **and** at least one fix type is on; the portal refuses to save the master switch with no type
ticked. Each suggestion gets its own uuid.

Created resources (`CdsFixResources`): MedicationRequest / ServiceRequest with `status: "draft"`,
`intent: "proposal"`, a `category` (MedicationRequest: `medicationrequest-category` `outpatient`; ServiceRequest:
SNOMED CT `108252007` "Laboratory procedure", since the only ServiceRequest fix is the care-gap test order), the
mapped code as `medicationCodeableConcept` / `code` (coding + text), `subject: Patient/<context.patientId>`;
Condition with the category, code, subject, and `encounter` for encounter diagnoses.

### Settings and validation

`CdsFixSettings { Enabled, FixTypes[ { Key, Enabled, Mappings[ { Trigger?, System, Code, Display? } ] } ],
RecommendedFixType? }` stored in `FixesJson` (null = off).

- Domain (`CdsServiceOptionsRules.NormalizeFixes`): unique fix type keys; ≤ 50 mappings per type; no two mappings
  for the same trigger (or two "any"); lengths (trigger 100, system 200, code 100, display 200); the recommended
  fix must be a switched-on type.
- Application (`CdsServiceInstanceService.ValidateFixes`, against the handler's `SupportedFixTypes`): the type
  must be one the handler offers; a switched-on type that needs a code must have one; each mapping's code system
  must be one the descriptor allows, its trigger one of the descriptor's triggers, and required display present.

### Checks before a card leaves Segue (`CdsSuggestionRules`, in `CdsEvaluator.Sanitize`)

- label and uuid present; **exactly one action**;
- `delete`: `resourceId` must be `Type/id` of an order present in this request's `context.draftOrders`;
- `create`: must carry a resource; an order (MedicationRequest / ServiceRequest / ProcedureRequest) must have
  `status: "draft"`;
- any other action type is dropped; fixes off → all dropped; at most 10 per card.

The call log records `SuggestionsOffered` (from the cards actually sent) and, from feedback,
`SuggestionsAccepted`.

## 12. Dismiss reasons and card links

**Dismiss reasons** (`overrideReasons` on every card): per instance in `OverrideReasonsJson`; null = the defaults
`CdsOverrideReasonOption.Defaults` (`patient-tolerated`, `benefit-outweighs-risk`, `will-monitor`,
`not-applicable`, the v1 standard list). Rules: 1–10 reasons, code 1–50 of letters, digits, `-`, `_`, no
duplicates (case-insensitive), display 1–200. System `urn:segue:cds:override-reason`. Codes must stay stable: the
health system maps each to an Epic override reason. Feedback codes are counted in Activity.

**Card links** (per instance in `LinksJson`, added to every card after the handler's own links, e.g. prior-auth's
submission link): at most 10; label 1–100; url absolute **https** ≤ 1000; type `absolute` or `smart`;
`appContext` (≤ 1000) and `autolaunchable` allowed on **smart** links only (rejected on absolute links, never sent
for them). Smart links must be on the health system's SMART app allow-list; the setup sheet lists them.

## 13. Epic setup sheet

`GET /api/v1/cds/services/{id}/setup-sheet` (`cdshooks.view`, `CdsSetupSheetService`) returns
`CdsSetupSheetDto`, configuration only:

- service URL, feedback URL, default audience (= service URL), all built from `Cds:PublicBaseUrl` when set (else the
  admin's origin, with a warning);
- hook, enabled flag, `FetchMissingData`, `FixesEnabled`;
- prefetch keys with their FHIR query and `fetchedIfMissing` (true when fetch-missing-data is on, an allowed issuer
  is enabled with callbacks allowed, and a fallback query exists);
- the effective dismiss reasons and their system;
- one fix row per mapping of each switched-on fix type (one row with no code for a fix that needs none), with the
  recommended flag;
- the links;
- the issuers allowed to call (all issuers when the allow-list is empty): iss, JWKS URL / pasted keys, expected
  sub, expected aud (override else service URL), effective FHIR base URL, allow-callback, enabled;
- warnings: `Cds:PublicBaseUrl` not set; service off; rule type unavailable; no enabled issuer; enabled issuer with
  no JWKS URL (Epic's jku would be refused); enabled issuer with no expected sub; no callback possible (Epic must
  send every prefetch key); fixes on (review codes); not clinically reviewed; SMART links need the allow-list;
- the clinical sign-off (`isClinicallySignedOff`, `signedOffBy`, `signedOffAtUtc`, `signOffNote`; section 20).

## 14. Admin API (`api/v1/cds`)

All routes are `[Authorize]` plus `[StandardPermission]` in the new group `PermissionGroupCode.CdsHooks = 43`
(id `30000000-0000-0000-0000-000000000043`, Platform category, "CDS Hooks"). Codes: `cdshooks.view`, `.create`,
`.edit`, `.delete`, `.test`; created on next boot and granted to SuperAdmin. See
[06 — Adding permissions](06-adding-permissions-howto.md).

| Controller | Routes |
|---|---|
| `CdsServicesController` | `GET/POST /cds/services`, `GET/PUT/DELETE /cds/services/{id}`, `GET /cds/services/{id}/sample-request` (view), **`GET /cds/services/{id}/setup-sheet` (view, section 13)**, `POST /cds/services/{id}/test` (test), **`POST` and `DELETE /cds/services/{id}/sign-off` (edit, section 20)** |
| | A service DTO also carries `isClinicallySignedOff`, `signedOffBy`, `signedOffAtUtc`, `signOffNote` and `usesSampleReferenceData` (section 20); they are not part of the save body, so a PUT keeps the sign-off unless it changes reviewed content. A service DTO carries `settings` (stored values with defaults filled in, what the rules use) and `storedSettings` (only what an admin set). Clients save `storedSettings` back, and the portal sends a value equal to its default as `null`, so a service keeps following the handler's defaults when a release changes them (e.g. after clinical review of the SAMPLE data). It also carries `fetchMissingData`, `fixes` (+ `fixesEnabled`), `overrideReasons` (+ `overrideReasonsAreDefault`), `links` and the handler's `supportedFixTypes`. **PUT replaces the whole service**, so clients must send these back (null/empty reasons = defaults; null fixes = off). |
| `CdsTrustedIssuersController` | `GET/POST /cds/trusted-issuers`, `GET/PUT/DELETE /cds/trusted-issuers/{id}`, **`POST /cds/trusted-issuers/{id}/test-callback` (test, section 10)**. Delete is refused (400) while the issuer is on an instance's allow-list. Issuer DTOs carry `expectedSubject`, `fhirBaseUrl`, `allowCallback` (default true). |
| `CdsCatalogController` | **`GET /cds/readiness` (view, section 20)**, `GET /cds/handlers` (now with each handler's `supportedFixTypes`), `GET /cds/discovery-info` (base URL, whether `Cds:PublicBaseUrl` is set, discovery and per-service URLs), `GET /cds/activity` (paged, filter by service, kind, outcome, date; page size max 200; rows now include trigger action, Epic version, criteria id, implementation version, callback count/ms, fixes offered/accepted and override reason codes) |

The **Test** endpoint (`CdsTestService`) runs the same `CdsEvaluator` with **no JWT and no issuer**, also on
disabled instances, so it never calls back to a FHIR server; fixes are produced and checked exactly as live. It
writes no `CdsCallLog` row and no metric, only a governance audit entry (module `CdsHooks`, action `TestRun`,
entity `CdsService`) with outcome and counts.

## 15. Audit and PHI rules

- **Never logged or stored:** request bodies, `context`, `prefetch`, `fhirAuthorization` (incl. `access_token`),
  patient / user / encounter ids, FHIR resources (sent or fetched by callback), callback URLs, card and suggestion
  content, feedback `userComment` and Epic's `reasonDisplay` / `reasonTitle`, the JWT.
- **Recorded per live call (exactly one `CdsCallLog` row):** time, kind, service id and instance, handler key,
  hook, `hookInstance` (a random uuid from the EHR), `iss` and matched issuer, outcome, HTTP status, card count
  (or feedback item count), duration, a short non-PHI `Detail`, and the new metadata: Epic trigger action, Epic
  version, criteria id, implementation version (short tokens only), callback resource count and ms, suggestions
  offered, suggestions accepted, override reason codes with a count per code across a batched feedback call
  (stored as `code` or `code:n`, e.g. `will-monitor:5,45`; the Activity DTO returns `overrideReasonCodes` (distinct)
  and `overrideReasonCounts` ([{code, count}]), and the portal sums the counts). A `reason.code` that is not a short
  token falls back to Epic's `reasonCategory` extension. Feedback detail reads
  "Accepted: n, overridden: m[, fixes accepted: k] (reasons: up to five codes)."
- The log row is written with `CancellationToken.None`, so a client that hangs up still leaves its row; a
  failure to write is logged (type only) and never surfaced to the caller.
- Rejected calls (401/403) also raise a governance security event `CdsCallRejected` (Warning). Its Details are
  capped at the column's 1000 characters, with the caller-supplied `iss` cut to 200 and control characters
  replaced, so a hostile token can neither make the event fail to save nor forge log lines.
- The service id is logged as the matched instance's id, or else the requested id trimmed and lower-cased (how
  lookup and the Activity filter compare it); unknown ids are tagged `unknown` in metrics and logs.
- A failed call log insert is detached from the request's DbContext, so it cannot break the saves that follow.

### Security rules added with Epic support and Phase 2

| Risk | Rule |
|---|---|
| Keys from an attacker's URL | A token's `jku` must equal the issuer's configured JWKS URL; keys are only ever fetched from configured URLs. |
| Another Epic client reusing an `iss` | Optional expected `sub` per issuer. |
| SSRF via callback | Callback only to the trusted issuer's FHIR base, matched after normalisation; queries must stay under it; address guard (incl. NAT64 / 6to4 / IPv4-compatible IPv6), no proxy, no redirects; https only by default. |
| Token leak | `fhirAuthorization.access_token` is used only for that call's callback, sent only to that FHIR base, never logged, stored or traced. Test callback sends no token at all. |
| Patient ids in logs / traces | Callback HttpClient has no loggers and is excluded from HttpClient tracing; spans carry counts only. |
| Unwanted EHR changes | Fixes off by default (confirm dialog in the portal); suggestions dropped when off; delete only of a draft order present in the request; created orders must be drafts; one action per suggestion; `systemActions` only from the coverage requirements handler, only `update` of the request's own resources carrying coverage information. |
| PHI in feedback | Only short reason codes are recorded; `userComment`, displays and titles are never read into storage. |

## 16. Metrics and tracing

Meter and ActivitySource `FHIRBridge.Cds`, registered in `AddFhirBridgeObservability`, written through
`ICdsMetrics` (SharedKernel).

| Instrument | Type | Meaning |
|---|---|---|
| `fhirbridge.cds.calls` | counter | every invoke and feedback call |
| `fhirbridge.cds.cards` | counter | cards returned (invoke only) |
| `fhirbridge.cds.errors` | counter | calls that ended in a 5xx |
| `fhirbridge.cds.duration` | histogram (ms) | call duration |

Tags: `kind` (invoke / feedback), `hook`, `service_id` (or `unknown`), `outcome`. The evaluator opens a
`cds.evaluate` span tagged `cds.hook`, `cds.service_id`, `cds.handler`, `cds.card_count`, and a `cds.callback`
span tagged with counts only. Callback HTTP requests carry the `FhirBridgeCdsMetrics.UntracedRequest` option and
are skipped by the HttpClient trace instrumentation, so no URL holding a patient id reaches a span.

## 17. Configuration keys

| Key | Default | Where read | Purpose |
|---|---|---|---|
| `Cds:PublicBaseUrl` | (none; admin screens show the request origin) | System Settings, then config | Base of the discovery and service URLs, and of the expected `aud`. **Required**: without it CDS calls are refused (401) unless the issuer has an audience override. |
| `Cds:AllowPrivateJwksAddresses` | false | config | Lets JWKS URLs resolve to private / loopback / link-local addresses. |
| `Cds:TimeoutMs` | 1500 | System Settings, then config; per call | Time budget per evaluation (100–30 000). |
| `Cds:ClockSkewSeconds` | 60 | config | JWT clock skew. |
| `Cds:JwksCacheMinutes` | 60 | config | JWKS cache lifetime. |
| `Cds:JwksFetchTimeoutSeconds` | 5 | config | One JWKS fetch. |
| `Cds:MaxTokenLifetimeMinutes` | 60 | config | Longest `exp` accepted. |
| `Cds:CallbackTimeoutMs` | 900 | System Settings, then config; per call | Budget for the whole FHIR callback (all keys in parallel), clamped to 50 … `TimeoutMs` − 100. |
| `Cds:AllowInsecureCallback` | false | config | Lets the callback (and Test callback) use plain http. **Dev only**: the EHR's bearer token would travel in clear text. |
| `Cds:AllowPrivateCallbackAddresses` | false | config | Lets callback URLs resolve to private / loopback / link-local addresses (same guard as JWKS). |
| `Cds:CallbackMaxResponseBytes` | 4194304 (4 MB) | config | Largest callback response read per query; bigger counts as not fetched. |
| `RateLimiting:Cds:PermitPerWindow` | 600 | System Settings, then config; at startup | Requests per IP per window. |
| `RateLimiting:Cds:WindowMinutes` | 1 | System Settings, then config; at startup | Window length. |

## 18. Limits

- **Sample rule data.** Every rule set needs clinical (and, for prior auth, payer) review before real use; so do
  any fix code mappings.
- **Callback is best-effort.** It fetches only what the handler's fallback queries name, only from the trusted
  issuer's FHIR base, within ~0.9 s. A slow EHR gives a degraded empty answer, never a slow one.
- **Fixes are draft-level only.** Create/delete one resource per suggestion; Epic uses its own order defaults for
  created orders. The `remove-draft-order` fix removes MedicationRequests only. systemActions exist only for
  coverage requirements (section 23).
- **Six built-in handler types.** Admin-authored rules exist only for coverage requirements (payer rules, section
  23); any other new rule type is code (section 8).
- Hooks supported: `patient-view`, `order-select`, `order-sign`; plus `appointment-book`, `encounter-start`,
  `encounter-discharge` for coverage requirements only (`order-dispatch` is not offered, section 23).
- No Epic `card.detail.content-type` extension is sent (detail is markdown).
- Rate-limit changes need a restart; JWKS keys are cached in memory per API instance.

## 19. EHR notes

| EHR | Notes |
|---|---|
| **Epic** | Supports CDS Hooks as a CDS client but **does not read discovery**: send the health system's Epic team the **Epic setup sheet** for each service (section 13). Add their Epic as a trusted issuer with `iss` (their FHIR base, e.g. `https://host/api/FHIR/R4`), the **JWKS URL** their tokens name in `jku` (required in practice: a token's jku must match it), the **client id** as expected `sub`, and leave the FHIR base URL blank unless it differs from `iss`. Epic's `aud` is the service endpoint they enter, so `Cds:PublicBaseUrl` must be the address they use. See section 9 for the full Epic behaviour. |
| **athenahealth** | Supports CDS Hooks 2.0. Same steps: discovery URL to athena, athena's `iss` and keys into a trusted issuer. |
| **eClinicalWorks** | CDS Hooks support **unconfirmed**. Check with eCW before planning on it. |
| **Public sandbox** | `https://sandbox.cds-hooks.org`, `iss` `https://sandbox.cds-hooks.org`, JWKS `https://sandbox.cds-hooks.org/.well-known/jwks.json`. Calls from the **browser**, so Segue must be reachable from the tester's machine (CORS is open for this reason). The portal has a one-click "Add sandbox as trusted issuer". |

In every case the token's `aud` must match the service URL the EHR was given, so set `Cds:PublicBaseUrl` to the
address the EHR actually uses before registering.

## 20. Go-live readiness and clinical sign-off

### Readiness report

`GET /api/v1/cds/readiness` (`cdshooks.view`, `ICdsReadinessService` → `CdsReadinessService` in Infrastructure,
rules in `CdsReadinessRules`, pure and unit-tested) returns `CdsReadinessDto { status, checks[], generatedOnUtc }`.
Each check is `{ key, label, status, detail, howToFix }`; `status` is `ok | warning | blocker` and the report's
status is the worst check. `howToFix` is null when ok. Details name services and issuers (configuration) but never
hold secrets: no keys, pasted JWKS, tokens or connection strings. The portal shows it as the **Go-live readiness**
panel on Connect your EHR.

| Key | Blocker | Warning | Ok |
|---|---|---|---|
| `public-base-url` | `Cds:PublicBaseUrl` unset, not an absolute http(s) URL (or has a query / fragment), or `http` outside Development | `http` in Development | `https` |
| `replay-protection` | – | jti replay record in process memory (`CdsReplayGuard.IsShared` false: no `ConnectionStrings:Redis`) | Redis |
| `trusted-issuer` | no enabled trusted issuer | – | at least one enabled |
| `issuer-jwks-url` *(only with an enabled issuer)* | an enabled issuer whose vendor contains "Epic" has no JWKS URL (Epic always sends `jku`) | another vendor's enabled issuer has pasted keys only | every enabled issuer has a JWKS URL |
| `issuer-subject` *(idem)* | – | an enabled issuer has no expected `sub` | all have one |
| `issuer-fhir-base-url` *(idem)* | – | an enabled issuer that allows callbacks has no https `EffectiveFhirBaseUrl` (FhirBaseUrl, else iss) | all usable |
| `enabled-service` | no service switched on | – | at least one on |
| `fixes-sign-off` | – | a service with `FixesEnabled` has no clinical sign-off | none, or all signed off |
| `sample-data-sign-off` | – | a service whose handler `UsesSampleReferenceData` (all five built-in) has no clinical sign-off | none, or all signed off |
| `license` | not Active (Unlicensed, Expired, Invalid): CDS calls get 503 | Active but ends within 30 days; Grace | Active |
| `rate-limit` | `RateLimiting:Cds:PermitPerWindow` or `WindowMinutes` ≤ 0 | fewer than 60 calls per minute per client IP | otherwise |

The sign-off checks look at every service, on or off, so services waiting to be switched on are listed too. The
rate limit is read the way `Program.cs` reads it (System Settings, then config, defaults 600 / 1), but the running
limiter only picks up a change after a restart; the detail says so. Since all of `/api/v1` sits behind the license
gate, an unlicensed install cannot open the report at all, so `license` matters mostly for "ends soon".

### Clinical sign-off

Advisory, per service instance: it **never blocks** creating, saving or switching on a service.

- Columns `SignedOffBy` (the reviewing clinician's name and role as typed, 1–200), `SignedOffAtUtc`, `SignOffNote`
  (≤ 500, optional, no patient data). `IsClinicallySignedOff` = both name and time set (not mapped).
- `POST /api/v1/cds/services/{id}/sign-off` body `{ signedOffBy, note?, contentVersion }` → the updated service
  DTO; blank name, too-long values or no `contentVersion` → 400; unknown id → 404; **409 Conflict** when
  `contentVersion` is not the service's current one (someone saved new settings, fixes, dismiss reasons or links
  after the reviewer opened it). `contentVersion` is on every service DTO: a short SHA-256 fingerprint of the handler
  key, hook, settings, fixes, dismiss reasons and links (`CdsServiceInstance.ContentVersion`, not stored). The
  portal sends the version it showed and explains a 409 (reload, review again). `DELETE` on the same path clears it (no-op when none). Both need
  `cdshooks.edit`. Who recorded it is in the entity audit trail (`ModifiedBy`), separate from the reviewer's name.
- **Cleared automatically** inside `CdsServiceInstance.Update` when the content a clinician reviewed changes: the
  handler key, the hook, the settings (compared by JSON value, so key order and spacing do not count), the fixes, the
  dismiss reasons or the links. Title, service id, description, card source, on/off, the issuer allow-list and
  fetch-missing-data keep it (the Services list's on/off switch resends the whole service and must not clear it).
- A **coverage requirements** service's sign-off also covers the payers and payer rules it answers with: any payer
  or rule create, update or delete, and any applied import that changes something, clears the sign-off of every
  coverage requirements service (`CdsCoverageSignOff.ClearAsync` in `CdsCoverageAdminServices.cs`).
- Shown as a badge on the Services list ("Reviewed by X on date" / "Not clinically reviewed"), as its own section on
  the service setup page (outside the main form; disabled while the page has unsaved edits, since it covers the
  saved version; each action asks first), and on the Epic setup sheet (fields plus a "Not clinically reviewed"
  warning).

## 21. Go-live checklist (Epic)

The order that works for a first Epic customer. The readiness panel covers the Segue side of steps 2–4 and 7.

1. **Epic app and APIs.** The CDS client is registered in Epic with the **CDS Hooks Framework** API. When one-click
   fixes will be used, also select: MedicationRequest **Read**, **Create** and **Delete (Unsigned Order)**;
   ServiceRequest **Read**, **Create** and **Delete (Unsigned Order)**; Condition **Create** for **Problems** and
   for **Encounter Diagnosis**. For fetch-missing-data, the read (search) APIs for every prefetch resource the
   services use (AllergyIntolerance, MedicationRequest, Observation, Condition, Immunization, Procedure, Patient).
2. **HTTPS deployment.** Segue behind https on the address Epic will call, with `Cds:PublicBaseUrl` set to it (the
   token's `aud` is checked against it). Plain http is a blocker outside Development.
3. **Redis.** `ConnectionStrings:Redis` (with `ssl=true`), so the one-time token check holds across nodes and
   restarts.
4. **Register the Epic issuer** (Connect your EHR > Trusted issuers): `iss` = the organisation's FHIR base (e.g.
   `https://host/api/FHIR/R4`), JWKS URL = the URL Epic names in `jku`, expected `sub` = the CDS client id. Leave the
   FHIR base URL blank unless it differs from `iss`. Run **Test callback**.
5. **Hand over the Epic setup sheet** for each service (Download JSON). The health system configures in Epic: the
   OPA / BPA records that fire each hook, the service endpoint and `aud`, the prefetch, the mapping of each dismiss
   reason code to an Epic override reason, the preference list (or order set) items behind each fix code, and the
   SMART app allow-list for SMART links.
6. **Test in Epic's non-production environment**, per service: each check fires and shows the expected card;
   each fix adds or removes the right draft; feedback arrives (Activity shows **Feedback received** with reason
   codes); missing prefetch is fetched by callback (Activity shows the fetched count). Fix what Activity reports as
   Not signed in, Missing data or Error.
7. **Clinical sign-off.** A clinician reviews each service's rules, settings, fix codes, dismiss reasons and links
   with the Epic team; record it on the service setup page. Any later change to that content removes it.
8. **Go live one service at a time.** Switch on one service in production, watch **Activity** (outcomes, timings,
   feedback, override reasons) for a few days, then the next.

## 23. Coverage Requirements Discovery (Da Vinci CRD)

Built to the [Da Vinci CRD IG STU 2](https://hl7.org/fhir/us/davinci-crd/STU2/) (v2.0.1). Epic calls it through its
"CRD Request" outgoing CDS Hooks call. While the clinician orders (or books, or starts / discharges an encounter),
Segue tells them what the patient's payer requires — covered or not, prior authorization, documentation — and
writes the answer onto the order itself as the CRD **coverage-information** extension through a `systemAction`.
**DTR (Documentation Templates and Rules) and PAS (Prior Authorization Support) are out of scope**: Segue only
points at a DTR app (a link) and names the payer's Questionnaire.

### Roles: local rules and payer relay

Segue plays both CRD server roles, per payer:

- **Local rules** (default): Segue answers from the payer's `CdsCoverageRule` rows, edited in the portal or imported
  from CSV / JSON.
- **Relay** (`RelayEnabled` with an https `RelayServiceUrl`): Segue acts as an intermediary and forwards the call to
  the payer's own CRD service, signed with Segue's key, then returns the payer's answer. If the relay fails or times
  out, `FallbackToLocalRules` (default **on**) answers from local rules; off → no cards, outcome `Degraded`.
- Admin **Test** runs never relay (pasted data is never sent anywhere); the payer's **Test relay** button sends a
  synthetic request instead.

### Data model (migration `AddCdsCoverageRequirements`: SQL Server `20261016062127`, PostgreSQL `20261016062144`)

| Entity | Fields | Notes |
|---|---|---|
| `CdsPayer` | Name (≤200, unique among live rows, case-insensitive), **Identifiers** (1–20, each ≤200), **PlanValues** (0–50), IsEnabled, RelayEnabled, RelayServiceUrl (https, no query, ≤1000; required when RelayEnabled), RelayTimeoutMs (100–10 000, default 1000), FallbackToLocalRules (default true), **ForwardFhirAuthorization (default false)**, RelayClientId (≤200, the JWT `sub`) | `AuditableEntity<Guid>`, soft delete, RowVersion. Identifiers/PlanValues are primitive collections (JSON on SQL Server, `text[]` on PostgreSQL). Deleting a payer soft-deletes its rules. |
| `CdsCoverageRule` | PayerId, System (≤200), Code (≤100), CodePrefixMatch, Covered, PaNeeded?, DocNeeded?, DocPurposes[], InfoNeeded[], Reason? (≤500), PolicyUrl? (https), QuestionnaireCanonical? (url or url\|version), EffectiveFrom (date, default 2000-01-01), EffectiveTo? (date, inclusive), IsEnabled | Same base. Unique (PayerId, System, Code, EffectiveFrom) among live rows. No FK navigation (soft delete is handled by the payer service). |
| `CdsCallLog` (new columns) | SystemActionCount (default 0), PayerId?, PayerName? (200), CoverageDecisions? (200), RelayUsed?, RelayMs?, RelayOutcome? (50), CoverageAssertionCount? | Codes and configuration only, never order, coverage or patient data. |

Rule validation (`CdsCoverageRule.Validate`, shared by save and import): `covered` ∈ covered / not-covered /
conditional; `paNeeded` ∈ no-auth / auth-needed / performpa / conditional (**`satisfied` is refused**: it needs a
payer-issued `satisfied-pa-id` per patient); `docNeeded` ∈ clinical / admin / both / conditional; `docPurpose` ∈
withpa / withclaim / withorder / retain-doc / OTH (only with docNeeded); `infoNeeded` ∈ performer / location /
timeframe / contract-window / OTH (`OTH` is v3-NullFlavor "other", which the CRD value sets include; the file import
reads it in any case). The code **system is stored in canonical form** (`CdsCoverageCodes.CodeSystems.Canonical`:
trimmed, no trailing `/`, an OID alias replaced by its URL), so `urn:oid:2.16.840.1.113883.6.12` and
`http://www.ama-assn.org/go/cpt` are one system for the unique key, the import key and matching.
CRD invariants enforced: crd-ci-q2 (not-covered has no pa-needed), crd-ci-q3 (info-needed needs a conditional
value), crd-ci-q1 (questionnaire needs doc-needed).

### Where the code lives

| Layer | Files |
|---|---|
| Domain | `Entities/CdsPayer.cs`, `Entities/CdsCoverageRule.cs` (+ `CdsCoverageRuleValues`), `ValueObjects/CdsCoverageCodes.cs`, new columns in `Governance/CdsCallLog.cs` |
| Application | `Abstractions/Cds/ICdsHandlerPreparer.cs`, `Abstractions/Cds/CdsCoverageModels.cs` (decision, prepared data, rule snapshot + cache, relay client, JWKS provider interfaces), `Abstractions/Persistence/ICdsCoverageRepositories.cs`, `DTOs/Cds/CdsCoverageDtos.cs`, `Services/Cds/CdsCoverageAdminServices.cs` (payer and rule services, import/export), `Services/Cds/CdsCoverageRuleFile.cs` (CSV / JSON parser and writer) |
| Infrastructure | `Cds/Handlers/CoverageRequirementsCdsHandler.cs` (pure), `Cds/Handlers/CdsCrdItems.cs`, `Cds/Handlers/CdsCoverageInformation.cs` (extension builder / reader), `Cds/Coverage/CdsCoverageEngine.cs` (the preparer), `CdsCoverageMatcher.cs`, `CdsCoverageRuleCache.cs`, `CdsPayerRelayClient.cs`, `CdsRelaySigningKeyProvider.cs`, `CdsPayerRelayTestService.cs`, `Persistence/CdsCoverageRepositories.cs` (EF + in-memory) |
| Api | `Controllers/V1/CdsCoverageController.cs` (`CdsPayersController`, `CdsCoverageRulesController`), JWKS action on `Controllers/CdsHooksController.cs` |

### Hooks, prefetch and what is checked

| Hook | Items checked (`CdsCrdItems`) | Prefetch (all required) |
|---|---|---|
| `order-sign` | every order in `context.draftOrders` | `coverage`: `Coverage?patient={{context.patientId}}&status=active&_include=Coverage:payor` (the payor Organizations come along, so a payer can be matched by the Organization's identifier or name when `Coverage.payor` is only a reference) |
| `order-select` | the selected draft orders (`context.selections`), else all | `coverage` |
| `appointment-book` | `context.appointments` Appointments (codes from serviceType, appointmentType, serviceCategory) | `coverage` |
| `encounter-start`, `encounter-discharge` | the Encounter `context.encounterId` names (codes from type, serviceType) | `coverage`, `encounter`: `Encounter?_id={{context.encounterId}}` |

`order-dispatch` is **not offered** in v1: its `context.dispatchedOrders` are references, which no prefetch
template can fetch (tokens resolve to strings, not arrays), and EHRs rarely contain the orders in
`fulfillmentTasks`, so a live call would never find an order. (`CdsCrdItems` still reads the hook, for when an
order lookup is added.)

Missing prefetch uses the normal callback path (section 10). Only `coverage` is required so that a missing optional
resource never blanks the answer; `patient` is not requested (no rule reads it).

### Engine (`CdsCoverageEngine`, preparer for `coverage-requirements`)

1. **Coverage**: the active Coverages in prefetch `coverage` (status active or absent; cancelled / draft /
   entered-in-error ignored), in `Coverage.order` (primary first).
2. **Payer**: the first Coverage whose payor matches an **enabled** payer. Each payer identifier is compared
   (case-insensitive) with `payor.identifier.value`, `system|value` with the identifier's system and value, or a
   bare name with `payor.display` / the referenced Organization's `name` / `alias` (the Organization may be a Bundle
   entry or contained) and its identifiers. When `PlanValues` is not empty one must equal a `Coverage.class.value`.
   One coverage per call (the first match).
3. **Relay or local**: relay for a relay payer on live calls; else local rules.
4. **Rule match** per item (`CdsCoverageMatcher.MatchRule`, rules effective today, UTC): system equal (case-insensitive;
   URL and OID forms of CPT, HCPCS, LOINC, SNOMED, RxNorm, ICD-10-PCS, NDC are the same), then exact code beats prefix,
   a longer prefix beats a shorter one, ties go to the latest EffectiveFrom.

The payer/rule **snapshot** (`CdsCoverageRuleCache`, singleton) is rebuilt after any admin change on this node
(`Invalidate`) and at most 60 s after it was built (other nodes).

### Answer (pure `CoverageRequirementsCdsHandler`)

Per item with a decision:

- **systemAction** `{ type: "update", description: "Coverage information from <payer>", resource: <copy of the order
  with the extension> }` when `useSystemActions` is on, the item has an id, the Coverage has an id, and the type is
  ServiceRequest, ProcedureRequest, DeviceRequest, NutritionOrder or Appointment. **MedicationRequest is never
  updated** (Epic supports ServiceRequest.Update (Unsigned Order) only, Feb 2024+): it gets an instructions card.
  An existing coverage-information extension for the same coverage is replaced; other extensions are kept. No
  action is sent when the order already carries the same covered / pa-needed / doc-needed for that coverage (CRD:
  no update unless something is new).
- **Extension** `http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information`, sub-extensions in
  this order: `coverage` (valueReference `Coverage/<id>`), `covered` (valueCode), `pa-needed`, `doc-needed`,
  `doc-purpose`*, `info-needed`*, `reason` (valueCodeableConcept.text), `questionnaire` (valueCanonical, only with
  doc-needed), `date` (valueDate, today), `coverage-assertion-id` (valueString, a new uuid per order).
- **Instructions card** (`includeInstructionsCard`, default on; always when no systemAction carries the decision):
  summary such as "Prior authorization required by &lt;payer&gt;: &lt;order&gt;", markdown detail listing coverage,
  prior authorization, documentation (purposes), information needed and the reason; indicator warning for
  not-covered, `authNeededIndicator` (warning) for auth-needed / performpa, else info. The rule's **policy URL** is
  an absolute link on it (when the instructions card is off: a separate "external reference" card with the link).
- **Documentation (DTR) card** when doc-needed is set and `dtrAppUrl` is an https URL: a `smart` link (appContext
  JSON `{"order":"Type/id","coverage":"Coverage/id","questionnaire":["<canonical>"]}`) or `absolute` link.
- No rule (or no coverage / no payer): nothing, or with `noRuleBehavior = conditional` an info card "Coverage
  requirements unknown".

**Relay answers** (a payer's answer is never trusted as it is):

- **Cards**: the topic follows `topicCodes` (segue: letters-only, `coverage-info` → `CoverageInformation`, another
  CRD card type such as `therapy-alternatives-opt` → `PayerTherapyAlternativesOpt`, none → `PayerCoverage`; crd: the
  payer's CRD code when it is one, else `coverage-info`); links are kept only when absolute https, and a `smart` link
  only when it launches this service's own `dtrAppUrl` (same scheme, host and path; appContext ≤ 2048); the payer's
  icon and card `extension` are dropped. Then the normal card clean-up (uuid, summary, https-only links, at most 25),
  labelled "&lt;payer&gt; via Segue".
- **systemActions are rebuilt, never forwarded**: from each payer action that names one of this request's items,
  only its coverage-information extensions are taken, each through `CdsCoverageInformation.Clean` (required:
  `coverage` naming one of this request's Coverages, `covered` from the value set, a valid `date` and a
  `coverage-assertion-id`; kept when well formed: pa-needed, doc-needed, doc-purpose, info-needed, reason (text and
  codings), questionnaire (http(s), only with doc-needed), satisfied-pa-id (only with pa-needed satisfied),
  billingCode; anything else, including nested or unknown sub-extensions, is dropped). They are then added with
  `Annotate` to **the request's own copy** of the order, so the payer can add coverage information but cannot change
  the code, status, quantity or any other part of the order. One action per item; none when nothing would change.
- When this service may not send a systemAction for the item (systemActions off, or a MedicationRequest), the
  cleaned coverage information is shown as an instructions card so it is not lost.

Settings (`GET /api/v1/cds/handlers`, key `coverage-requirements`):

| Key | Type | Default | Meaning |
|---|---|---|---|
| `useSystemActions` | boolean | true | Annotate orders / appointments with systemActions. |
| `includeInstructionsCard` | boolean | true | Plain-language card per decision. |
| `noRuleBehavior` | select `none` \| `conditional` | none | What to show when no rule / payer / coverage. |
| `dtrAppUrl` | text | "" | https launch URL of the DTR app (empty = no DTR card). |
| `dtrLinkType` | select `smart` \| `absolute` | smart | DTR link type. |
| `topicCodes` | select `segue` \| `crd` | segue | Epic-safe letters-only topics, or the CRD card type `coverage-info` (system `http://hl7.org/fhir/us/davinci-crd/CodeSystem/temp`). |
| `authNeededIndicator` | select info \| warning \| critical | warning | Card level for prior authorization needed. |

`UsesSampleReferenceData` is false (payer rules are admin data), so the sample-data sign-off does not apply; the
`crd-sign-off` readiness check asks for a review instead. `GET /api/v1/cds/handlers` reports it per handler
(`usesSampleReferenceData`), so the portal shows its sample-data warning only for checks that use sample rules.

### Relay (`CdsPayerRelayClient`)

- **Outbound request**: same hook, **new hookInstance**, context and prefetch as received plus what the callback
  fetched; no vendor extensions; `fhirServer` and `fhirAuthorization` **only when the payer's
  ForwardFhirAuthorization is on** (off by default — the EHR's token is never forwarded otherwise).
- **JWT** (Segue's own key): header `alg: RS384`, `typ: JWT`, `kid`, `jku = {Cds:PublicBaseUrl}/cds-services/.well-known/jwks.json`;
  claims `iss = Cds:PublicBaseUrl`, `aud = RelayServiceUrl`, `sub = RelayClientId` (when set), `iat`, `nbf`,
  `exp = iat + Cds:RelayTokenLifetimeSeconds` (60, clamped 10–300), `jti` (uuid). Without a configured
  `Cds:PublicBaseUrl` nothing is relayed ("Cds:PublicBaseUrl is not set").
- **Key** (`CdsRelaySigningKeyProvider`): RSA 2048 created on first use; private key (PKCS#8 PEM) written via
  `ISecretWriter` to the `signing-keys` vault (DbSecretStore / Key Vault); only `{vault, secret, kid}` is stored, in
  System Setting `Cds:RelaySigningKey`. A stored reference whose secret is unusable is an error, never silently
  replaced (delete the setting to rotate, then tell payers to refresh the JWKS). **Creation is atomic across Segue
  instances**: the reference is written with `ISystemSettingRepository.AddIfAbsentAsync` (insert only, on the unique
  `IX_SystemSettings_Key`), so when two instances create a key at the same moment the loser adopts the winner's key
  and every instance signs with, and publishes, the same key (the loser's unused secret is logged by name and can
  be deleted).
- **JWKS**: `GET /cds-services/.well-known/jwks.json`, anonymous, `Cache-Control: public, max-age=3600`, one RSA key
  (`kty`, `use: sig`, `alg: RS384`, `kid`, `n`, `e`). 503 when the key cannot be read. Like every `/cds-services`
  path it is behind the license gate.
- **SSRF / transport**: https only; primary handler `CdsJwksHttpHandler.CreateGuarded` (no private, loopback,
  link-local or CGNAT addresses unless `Cds:AllowPrivateRelayAddresses`; no redirects; no proxy); no loggers, no
  `ApiRequestLoggingHandler`, no resilience handler (no retries); requests marked untraced.
- **Timeout**: `min(RelayTimeoutMs, remaining call budget − 150 ms)`; under 100 ms left → not called. Raise
  `Cds:TimeoutMs` (default 1500) for slow payers.
- **Response**: capped at `Cds:RelayMaxResponseBytes` (1 MB); must be JSON with a `cards` array. Outcomes: `ok`,
  `timed out`, `HTTP <n>`, `unreachable`, `response too large`, `invalid response`, `not https`,
  `signing key unavailable`, `Cds:PublicBaseUrl is not set`, `no time left in the call's budget`.

### Admin API (all `[Authorize]`, group CdsHooks)

| Method and path | Permission | Body / query → result |
|---|---|---|
| `GET /api/v1/cds/payers` | `cdshooks.view` | → `CdsPayerDto[]` |
| `GET /api/v1/cds/payers/{id}` | view | → `CdsPayerDto` / 404 |
| `POST /api/v1/cds/payers` | `cdshooks.create` | `SaveCdsPayerRequest` → 201 `CdsPayerDto` |
| `PUT /api/v1/cds/payers/{id}` | `cdshooks.edit` | `SaveCdsPayerRequest` (replaces all) → `CdsPayerDto` |
| `DELETE /api/v1/cds/payers/{id}` | `cdshooks.delete` | → 204 (rules deleted too) |
| `POST /api/v1/cds/payers/{id}/test-relay` | `cdshooks.test` | → `CdsPayerRelayTestResultDto` (audited `CdsHooks/TestRelay`) |
| `GET /api/v1/cds/coverage-rules?payerId&code&search&enabled&page&pageSize` | view | → `CdsCoverageRulePageDto` (page size default 50, max 200) |
| `GET /api/v1/cds/coverage-rules/{id}` | view | → `CdsCoverageRuleDto` / 404 |
| `POST /api/v1/cds/coverage-rules` | create | `SaveCdsCoverageRuleRequest` → 201 |
| `PUT /api/v1/cds/coverage-rules/{id}` | edit | `SaveCdsCoverageRuleRequest` → `CdsCoverageRuleDto` (payer cannot change) |
| `DELETE /api/v1/cds/coverage-rules/{id}` | delete | → 204 |
| `POST /api/v1/cds/coverage-rules/import?format&mode&strategy&payerId` | edit (**plus `cdshooks.delete` for `mode=apply&strategy=replace`**, 403 without: replace deletes rules) | multipart field `file` or raw body (≤5 MB, ≤5000 rules) → `CdsCoverageRuleImportResultDto` |
| `GET /api/v1/cds/coverage-rules/export?format&payerId` | view | file download (csv `text/csv` / json `application/json`) |
| `GET /api/v1/cds/coverage-rules/import-template?format` | view | file download with two made-up rows |

Import: `mode=preview` (default) writes nothing; `mode=apply` saves **all or nothing** (any row error → nothing).
`strategy=merge` (default) adds new and updates matching rules (key: payer + canonical system + code +
effectiveFrom, case-insensitive); `strategy=replace` also deletes the rules of every payer named in the file (or `payerId`) that
the file does not list. `payerId` makes every row belong to that payer (payer column optional, must match when set).
Validation errors are 400 `BusinessRuleException` like the other CDS admin endpoints.

**CSV columns** (header row; any order, case-insensitive; `system`, `code`, `covered` required, `payer` required
without `payerId`): `payer, system, code, codeMatch (exact|prefix, default exact), covered, paNeeded, docNeeded,
docPurpose (;-separated), infoNeeded (;-separated), reason, policyUrl, questionnaire, effectiveFrom (yyyy-MM-dd),
effectiveTo (yyyy-MM-dd), enabled (true|false|yes|no|1|0, default true)`. RFC 4180 quoting; UTF-8 (BOM allowed).
Export prefixes cells starting with `=`, `+`, `@`, tab, CR or a non-numeric `-` with `'` (import strips it). JSON:
`{"rules":[{ same names; docPurpose / infoNeeded arrays or ;-strings; enabled boolean }]}` or a bare array.

### Call log, Activity, readiness, setup sheet

- **Activity** rows (`CdsCallLogDto`) add: `systemActionCount`, `payerId`, `payerName`, `coverageDecisions`
  (string[] "covered/pa-needed/doc-needed", `-` = not stated), `relayUsed` (null for non-CRD calls), `relayMs`,
  `relayOutcome` (`ok`, a failure reason, or "&lt;reason&gt;; local rules used"), `coverageAssertionCount`.
  Detail carries the engine's note (e.g. "No enabled payer matches the patient's coverage.").
- **Readiness** (only when a coverage requirements service or a payer exists): `crd-payer-coverage` (blocker: a
  CRD service is on and no payer is on; warning: an enabled payer with no rule **in effect today** (enabled and
  within its dates) and no relay),
  `crd-relay-https` (blocker: relay URL not https; warning: a payer receives the EHR's token), `crd-rules-expiring`
  (warning: enabled rules of enabled payers ending within 30 days), `crd-sign-off` (warning: CRD services without
  sign-off).
- **Setup sheet**: `crd` section (null for other services): `hook`, `epicApis` (CDS Hooks Framework; Coverage.Search;
  ServiceRequest.Update (Unsigned Order) when systemActions are on; Encounter.Search for encounter hooks),
  `useSystemActions`, `dtrAppUrl`, `dtrLinkType`, `segueJwksUrl`, `payers[]` (`id, name, isEnabled, identifiers,
  planValues, enabledRuleCount, relayEnabled, relayServiceUrl, forwardFhirAuthorization`) and `notes[]` (configure
  the CRD Request with Coverage prefetch, MedicationRequests get cards, add the DTR app to the SMART allow-list, payers
  verify relays with Segue's JWKS). A warning is added when no payer is on.

### Configuration keys (added)

| Key | Default | Purpose |
|---|---|---|
| `Cds:AllowPrivateRelayAddresses` | false | Lets relay URLs resolve to private / loopback addresses. |
| `Cds:RelayMaxResponseBytes` | 1048576 | Largest payer answer read. |
| `Cds:RelayTokenLifetimeSeconds` | 60 | Lifetime of Segue's relay JWT (10–300). |
| `Cds:RelaySigningKey` (System Setting, written by Segue) | – | Reference to the relay signing key. Do not edit. |

### Differences from the IG and judgment calls

- coverage-information has **no `expiry-date`** in STU2; validity is in `detail` (not produced in v1). `date`
  (1..1) is today. `billingCode`, `detail`, `dependency`, `response`, `contact` and `satisfied-pa-id` are not
  produced by local rules. From a relayed payer, `billingCode` and `satisfied-pa-id` are kept (cleaned); `detail`,
  `dependency`, `response` and `contact` are dropped (the rebuilt extension keeps only what Segue can check).
- The IG says DTR launches go through coverage-information (`questionnaire`), not launch cards. Segue sends the
  questionnaire in the extension **and**, when `dtrAppUrl` is set, a DTR link card (Epic shows cards, not the
  extension).
- The IG binds `source.topic` to CRD card types (`coverage-info`, hyphenated); Epic needs letters-only static codes.
  Default `topicCodes = segue`; `crd` gives the IG codes.
- The IG prefetch examples also include encounter / appointment with `_include`s and use `{{context.patient}}`;
  Segue requires only `coverage` (`{{context.patientId}}`, with `_include=Coverage:payor`), plus `encounter` for
  encounter hooks.
- One Coverage per call (the first matching, in `order`); the IG allows one extension per coverage.
- `satisfied` is refused in local rules (needs a per-patient satisfied-pa-id).
- Not built in v1 (later): propose-alternate-request, companions / prerequisites, request-form-completion,
  create-or-update-coverage-information cards, `order-dispatch` order lookup by query, DTR and PAS themselves, the
  CRD metric reporting, configuration options in discovery (`extension.davinci-crd.configuration-options`).
