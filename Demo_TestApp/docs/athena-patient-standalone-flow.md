# athenahealth Patient Standalone flow ("Connect Get Data")

How the demo app's **Connect Get Data** button, when the athenahealth vendor toggle is
selected, drives a real patient-facing SMART on FHIR Standalone launch through FHIRBridge
and pulls that patient's data back into the demo app. This is also the general pattern for
adding any other EHR vendor to the same screen — Epic/MyChart and eCW already use the
identical mechanism, just with their own Workflow Id / Base URL / EhrEndpoint Id.

Source: `Demo_TestApp/frontend/src/app/demo-types/patient-standalone/` (component:
`launch-standalone-patient.ts`, service: `core/services/patient-standalone-launch.service.ts`)
and FHIRBridge's `PatientStandaloneLaunchController` / `WorkflowEndpoints`
(`src/Api/FHIRBridge.Api/`).

## 1. What has to exist in FHIRBridge first

Before the button can do anything, an admin must have, in FHIRBridge itself:

1. An **Athenahealth source connection** with `ApplicationType = Patient` (authorization-code
   + PKCE, patient-facing — not the Backend Services / system-JWT connection type). See
   `PatientApplicationStrategy` (`src/Runtime/FHIRBridge.Runtime.Infrastructure/Applications/`).
2. A **workflow** whose source node uses that connection, and that has been opted into public
   launch: `POST /api/v1/workflows/{workflowId}/enable-public-launch`. Without this,
   `public-patient-standalone-url` and the anonymous `/run` both 404 the workflow away rather
   than reveal it exists.
3. An **`EhrEndpoint` row of type `MyChart`** whose FHIR base URL is athenahealth's sandbox
   endpoint. athenahealth's Patient audience has exactly one fixed FHIR base URL (no
   per-hospital directory the way Epic does), so there is nothing for the user to pick from —
   this row exists purely so `public-patient-standalone-url` has an id to mint an authorize URL
   against. See `EhrEndpointType` — Patient Standalone only ever launches against `MyChart`-type
   rows, never the shared vendor-sandbox rows Provider Standalone uses.

An admin then pastes that workflow's **id** and FHIRBridge's **base URL** (plus the
`EhrEndpoint` id) into the demo app's own Admin Settings screen
(`demo-types/admin-settings/admin-settings.html`, "athenahealth — Patient Standalone" section):

| Field | Meaning |
|---|---|
| Athena Patient Standalone Workflow Id | The FHIRBridge workflow id from step 2 above |
| Athena Patient Standalone Base URL | FHIRBridge's own base URL, e.g. `https://localhost:5000` |
| Athena EhrEndpoint Id | The `MyChart`-type `EhrEndpoint` row id from step 3 above |

These three values are stored in the demo app's own database
(`WorkflowSettingsEntity.AthenaWorkflowId` / `AthenaBaseUrl` / `AthenaEhrEndpointId`) and served
back to the frontend by `GET /api/patient-standalone-settings`.

## 2. Request flow end-to-end

```
Patient (browser)                    Demo App backend           FHIRBridge
     |                                      |                        |
     | 1. Load page, pick "athenahealth"    |                        |
     |    vendor tab, click                 |                        |
     |    "Connect Get Data"                |                        |
     |------------------------------------->|                        |
     |   GET /api/patient-standalone-       |                        |
     |   settings (already cached from      |                        |
     |   page load)                         |                        |
     |                                       |                        |
     | 2. Frontend calls FHIRBridge directly (no demo backend hop):  |
     |    GET {athenaBaseUrl}/api/v1/workflows/{athenaWorkflowId}/   |
     |        validate-run   (pre-flight, records the attempt)       |
     |------------------------------------------------------------->|
     |<-------------------------------------------------------------|
     |   { isValid, correlationId, errors: [] }                      |
     |                                                                |
     |    GET {athenaBaseUrl}/api/v1/workflows/{athenaWorkflowId}/   |
     |        token-status?callerId=...                              |
     |------------------------------------------------------------->|
     |<-------------------------------------------------------------|
     |   { hasValidToken: false }   (first visit — no token yet)     |
     |                                                                |
     | 3. No valid token -> mint a launch URL and redirect:          |
     |    GET {athenaBaseUrl}/api/v1/workflows/{athenaWorkflowId}/   |
     |        public-patient-standalone-url                          |
     |        ?ehrEndpointId={athenaEhrEndpointId}                   |
     |        &callerId={this page's own URL}                        |
     |        &sessionId={persisted or omitted}                      |
     |        &userIdentity={demo app account email}                 |
     |------------------------------------------------------------->|
     |<-------------------------------------------------------------|
     |   { launchUrl, sessionId, mode: "patient", ... }               |
     |                                                                |
     |  window.location.href = launchUrl   (full-page navigation)    |
     |------------------------------------------------------------->|
     |                                          FHIRBridge redirects |
     |                                          on to athenahealth's |
     |                                          real patient login   |
     |<===============================================================
     |         Patient logs into athenahealth, authorizes access      |
     |=================================================================>
     |                                          athenahealth redirects|
     |                                          back to FHIRBridge's  |
     |                                          /oauth/callback with  |
     |                                          code + state          |
     |                                          FHIRBridge exchanges  |
     |                                          the code for a token, |
     |                                          caches it keyed by    |
     |                                          sessionId, and        |
     |                                          redirects the browser|
     |                                          back to callerId with |
     |                                          ?workflowRunId=...    |
     |                                          or ?signedIn=1        |
     |<-------------------------------------------------------------|
     | 4. Page reloads at callerId?workflowRunId=... (or ?signedIn=1)|
     |    Frontend calls:                                            |
     |    GET {athenaBaseUrl}/api/v1/workflows/runs/{workflowRunId}/ |
     |        launch-result       (only if workflowRunId present)    |
     |------------------------------------------------------------->|
     |<-------------------------------------------------------------|
     |   { patientId, patient }                                       |
     |                                                                |
     | 5. Token is now valid -> fetch the actual data:                |
     |    POST {athenaBaseUrl}/api/v1/workflows/{athenaWorkflowId}/  |
     |         run                                                    |
     |         { patientId, patientSearchCriteria: null, callerId }   |
     |------------------------------------------------------------->|
     |<-------------------------------------------------------------|
     |   { workflowRun: { status: "Succeeded" }, outputsByNodeId }    |
     |                                                                |
     | 6. Frontend extracts Patient resources from the Source node's |
     |    output and renders the list.                               |
```

Two things worth calling out because they are easy to get wrong when replicating this pattern
for a new vendor:

- **`callerId` has two unrelated meanings** depending on which call it's sent on. On
  `public-patient-standalone-url` it's the browser URL FHIRBridge's `/oauth/callback` should
  redirect back to once the token exchange completes. On `token-status`/`validate-run`/`run`/
  `discard-token` it's the *interactive token cache key* (`SmartAuthorizationCodeTokenProvider`
  keys its cache on this, not on the source connection id) — send the **same** value on every
  call for one browser session, or a token that really is cached will look missing.
- **`sessionId`** (a separate field, only on `public-patient-standalone-url`) is what actually
  becomes that cache key server-side. Persist whatever FHIRBridge returns (`localStorage`, since
  a full-page navigation to the vendor's login and back would wipe an in-memory value) and echo
  it back as `callerId` on every later token-status/validate-run/run/discard-token call. (Yes,
  the field is literally named `sessionId` when minting and `callerId` everywhere else — same
  value, FHIRBridge's existing wire shape, not a demo-app inconsistency.)

## 3. API reference

All calls target `{athenaBaseUrl}` (FHIRBridge), anonymous (no cookie/bearer token) except
where noted — gated instead on the workflow's `enable-public-launch` opt-in.

| Method & path | Purpose | Key request fields | Key response fields |
|---|---|---|---|
| `POST /api/v1/workflows/{workflowId}/validate-run` | Pre-flight check; records the attempt in Execution History even if never run | `patientId`, `patientSearchCriteria`, `callerId`, `ehrEndpointId` | `isValid`, `correlationId`, `errors[]` |
| `GET /api/v1/workflows/{workflowId}/token-status?callerId=` | Cheap check: is there already a usable cached token? | query: `callerId`, `patientId` | `hasValidToken` |
| `GET /api/v1/workflows/{workflowId}/public-patient-standalone-url` | Mints the athenahealth authorize URL to redirect the browser to | query: `ehrEndpointId`, `callerId`, `sessionId`, `userIdentity` | `launchUrl`, `sessionId`, `mode`, `applicationType` |
| `GET /api/v1/workflows/runs/{workflowRunId}/launch-result` | Resolves which patient FHIRBridge's own post-login convenience run found (if any) | — | `patientId`, `patient` |
| `POST /api/v1/workflows/{workflowId}/run` | Actually executes the workflow and returns the fetched resources | `patientId`, `patientSearchCriteria`, `callerId` | `workflowRun.status`, `workflowRun.errorMessage`, `outputsByNodeId` |
| `POST /api/v1/workflows/{workflowId}/discard-token` | "Reset Token" — discards FHIRBridge's cached token for this session, forcing a fresh login next time | query: `callerId`, `patientId` | — |

`run`/`validate-run`/`discard-token` additionally require FHIRBridge's double-submit CSRF
cookie echoed back as `X-CSRF-Token` (the cookie itself, `fhirbridge_csrf`, is deliberately not
`HttpOnly` so a legitimate browser caller can read and echo it).

## 4. Code sample

Condensed from the real service (`patient-standalone-launch.service.ts`) — TypeScript/Angular
`HttpClient`, but the shape is the same for any HTTP client:

```ts
const FHIRBRIDGE_BASE_URL = athenaBaseUrl;      // from admin settings
const WORKFLOW_ID = athenaWorkflowId;           // from admin settings
const EHR_ENDPOINT_ID = athenaEhrEndpointId;    // from admin settings

function getCsrfToken(): string {
  const match = document.cookie.match(/(?:^|; )fhirbridge_csrf=([^;]*)/);
  return match ? decodeURIComponent(match[1]) : '';
}

// Persisted across the full-page redirect to athenahealth and back.
let sessionId = localStorage.getItem('athenaSessionId') ?? undefined;
let patientId: string | null = null;

async function connectGetData() {
  // 1. Pre-flight (optional but recommended — surfaces config problems before bouncing the
  //    user through a login that was never going to work).
  const validation = await fetch(
    `${FHIRBRIDGE_BASE_URL}/api/v1/workflows/${WORKFLOW_ID}/validate-run`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': getCsrfToken() },
      credentials: 'include',
      body: JSON.stringify({ patientId, patientSearchCriteria: null, callerId: sessionId ?? null }),
    },
  ).then(r => r.json());

  if (!validation.isValid) {
    throw new Error(validation.errors.map((e: any) => e.message).join(' '));
  }

  // 2. Is there already a usable token for this session?
  const params = new URLSearchParams();
  if (sessionId) params.set('callerId', sessionId);
  if (patientId) params.set('patientId', patientId);
  const { hasValidToken } = await fetch(
    `${FHIRBRIDGE_BASE_URL}/api/v1/workflows/${WORKFLOW_ID}/token-status?${params}`,
    { credentials: 'include' },
  ).then(r => r.json());

  if (!hasValidToken) {
    // 3. No token — mint a launch URL and hand the browser off. This function never returns;
    //    the browser navigates away to athenahealth's login, then back to FHIRBridge's own
    //    /oauth/callback, then to callerId below with ?workflowRunId=... or ?signedIn=1.
    const mintParams = new URLSearchParams({ ehrEndpointId: EHR_ENDPOINT_ID });
    mintParams.set('callerId', window.location.origin + window.location.pathname); // OAuth redirect-back URL
    if (sessionId) mintParams.set('sessionId', sessionId);

    const mint = await fetch(
      `${FHIRBRIDGE_BASE_URL}/api/v1/workflows/${WORKFLOW_ID}/public-patient-standalone-url?${mintParams}`,
    ).then(r => r.json());

    localStorage.setItem('athenaSessionId', mint.sessionId); // persist BEFORE navigating away
    window.location.href = mint.launchUrl;
    return;
  }

  // 4. Token is valid — fetch the data.
  const result = await fetch(
    `${FHIRBRIDGE_BASE_URL}/api/v1/workflows/${WORKFLOW_ID}/run`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': getCsrfToken() },
      credentials: 'include',
      body: JSON.stringify({ patientId, patientSearchCriteria: null, callerId: sessionId ?? null }),
    },
  ).then(r => r.json());

  if (result.workflowRun.status !== 'Succeeded') {
    throw new Error(result.workflowRun.errorMessage ?? 'Workflow run failed.');
  }

  // 5. Pull the Patient resource(s) out of the Source node's output.
  const sourceOutput = Object.values(result.outputsByNodeId ?? {})
    .find((o: any) => o.nodeType.endsWith('SourceNode'));
  const patients = (sourceOutput?.payload?.resources ?? [])
    .filter((r: any) => r.resourceType === 'Patient');

  return patients;
}

// Called once, on page load, after the full-page redirect back from athenahealth:
async function handleOAuthReturn() {
  const params = new URLSearchParams(window.location.search);
  const workflowRunId = params.get('workflowRunId');
  window.history.replaceState(null, '', window.location.pathname); // strip so a refresh doesn't replay this

  if (workflowRunId) {
    const { patientId: resolved } = await fetch(
      `${FHIRBRIDGE_BASE_URL}/api/v1/workflows/runs/${workflowRunId}/launch-result`,
      { credentials: 'include' },
    ).then(r => r.json());
    patientId = resolved;
  }

  return connectGetData(); // token is now cached — this call fetches immediately, no redirect
}
```

## 5. How this generalizes to Epic and eCW (and any future vendor)

The demo app's `launch-standalone-patient.ts` resolves three values — **Workflow Id**,
**Base URL**, **EhrEndpoint Id** — from a small vendor switch, and every call above
(`validate-run`, `token-status`, `public-patient-standalone-url`, `run`, `discard-token`) takes
those three as parameters rather than hardcoding Epic's. Adding a fourth vendor to this screen
is exactly: register its own Patient-application-type source connection and workflow in
FHIRBridge, opt that workflow into public launch, seed one `MyChart`-type `EhrEndpoint` row for
it (or reuse Epic's per-hospital directory if the vendor has one — see `loadHospitals()`/`GET
/api/v1/ehr-public-endpoints?endpointType=MyChart`, Epic's only), add its own three admin-config
fields, and add one more branch to the vendor switch. No FHIRBridge API surface is vendor-
specific — `Base URL` is what makes this multi-tenant/multi-region-safe too (Epic and
athenahealth here happen to point at the same FHIRBridge instance, but the pattern supports
pointing different vendors at entirely different FHIRBridge deployments).

| | Workflow Id field | Base URL field | EhrEndpoint Id field | Has a hospital picker? |
|---|---|---|---|---|
| Epic/MyChart | `PatientWorkflowId` | `PatientBaseUrl` | *(user picks from `GET /ehr-public-endpoints`)* | Yes — many hospitals |
| athenahealth | `AthenaWorkflowId` | `AthenaBaseUrl` | `AthenaEhrEndpointId` (fixed) | No — one sandbox endpoint |
| eCW (Healow) | `EcwWorkflowId` | `EcwBaseUrl` | `EcwEhrEndpointId` (fixed) | No — one practice endpoint |
