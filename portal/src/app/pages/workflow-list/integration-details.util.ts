import { WorkflowSummary } from '../../services/workflow-api.service';

/**
 * Builds the "Integration details" panel's contents for one workflow.
 *
 * Read by a third-party developer who has no access to this portal and nobody to ask, so it has to be complete and
 * honest about what will not work. The panel is organised around API clients (Settings > API Clients): the app
 * keeps a Client ID and Client Secret on ITS SERVER and uses them to trigger the workflow, in one of two ways -
 *
 *  1. Server to server: exchange the credentials for a short-lived token (client credentials grant) and call the
 *     run endpoint with it. Backend workflows only - there is no person to sign in.
 *  2. Browser redirect (launch ticket): the app's backend exchanges the credentials for a single-use, 90-second
 *     ticket and sends the user's browser to Segue's run page. Segue's page handles the EHR sign-in / consent when the
 *     workflow needs one, shows progress, and returns the browser to the app. The secret never reaches the browser.
 *
 * EHR launch workflows are the exception: they start INSIDE the hospital's EHR, so no credential can start them and
 * they keep their own launch sequence.
 *
 * Every refusal on the anonymous endpoints is a bare 404 or a generic error on purpose, so readiness checks surface
 * the conditions BEFORE the partner hits a dead end. Kept as plain functions so the per-audience decisions are
 * directly testable without standing up the component.
 */

/** One copyable line in the panel. */
export interface IntegrationValue {
  label: string;
  value: string;
  /** Longer explanation shown under the value — what this is for, or what to do with it. */
  hint?: string;
  /** Rendered as a monospace URL/code box rather than plain text. */
  code?: boolean;
}

/** One blocking or advisory readiness item — what must be true before a partner's call will succeed. */
export interface IntegrationCheck {
  label: string;
  /** ok = satisfied; blocked = the call will fail until this is fixed; note = advisory, not a blocker. */
  state: 'ok' | 'blocked' | 'note';
  detail: string;
}

export interface IntegrationDetails {
  workflowId: string;
  name: string;
  /** Friendly audience label, e.g. "Patient Standalone". */
  audience: string;
  /** How this workflow is executed — decides which half of the panel applies. */
  kind: 'backend' | 'standalone' | 'patient' | 'ehr-launch';
  summary: string;
  steps: string[];
  values: IntegrationValue[];
  checks: IntegrationCheck[];
}

export function buildIntegrationDetails(
  row: WorkflowSummary,
  origin: string,
  audienceLabel: string,
): IntegrationDetails {
  const id = row.workflowId;
  const checks = buildChecks(row, origin);

  const values: IntegrationValue[] = [
    { label: 'Workflow ID', value: id, hint: 'Identifies this workflow on every call below.', code: true },
    { label: 'Base address', value: origin, hint: 'The Segue instance your app’s server talks to.', code: true },
  ];

  if (row.workflowNumber) {
    values.push({
      label: 'Workflow number',
      value: row.workflowNumber,
      hint: 'Human-quotable reference — use it when raising a support request about this workflow.',
    });
  }

  // Backend is the only audience a server can start on its own. Everything else needs a person in a browser.
  if (row.action !== 'Launch') {
    return backendDetails(row, origin, audienceLabel, values, checks);
  }

  if (row.applicationType === 'EhrLaunch') {
    return ehrLaunchDetails(row, origin, audienceLabel, values, checks);
  }

  return standaloneDetails(row, origin, audienceLabel, values, checks);
}

/** What every API-client based integration needs set up once, before any call below can succeed. */
function apiClientChecks(origin: string): IntegrationCheck[] {
  return [
    {
      label: 'API client created',
      state: 'note',
      detail: 'An administrator creates one in Settings > API Clients and hands over the Client ID and Client Secret. '
        + 'The secret is shown once, so store it in your server’s secret store. One API client works for every '
        + 'workflow on this instance.',
    },
    {
      label: 'Your app’s pages registered as Allowed Caller URLs',
      state: 'note',
      detail: 'In the same screen, add the page address(es) your app triggers from and returns to - an exact page, or '
        + 'a whole domain. Browser-redirect calls are refused with “caller URL is not whitelisted” for any address '
        + `that is not on the list. (This instance: ${origin}.)`,
    },
  ];
}

/**
 * The conditions that otherwise produce a refusal with no explanation. Ordered so the two that apply to every
 * audience come first, then the audience-specific ones.
 */
function buildChecks(row: WorkflowSummary, origin: string): IntegrationCheck[] {
  const checks: IntegrationCheck[] = [
    row.status === 'Disabled'
      ? {
          label: 'Workflow enabled',
          state: 'blocked',
          detail: 'This workflow is disabled, so it will not run. Enable it from this row’s menu.',
        }
      : { label: 'Workflow enabled', state: 'ok', detail: 'This workflow is enabled.' },

    row.hasDestination
      ? { label: 'Destination configured', state: 'ok', detail: 'A destination is configured.' }
      : {
          label: 'Destination configured',
          state: 'blocked',
          detail: 'No destination is wired up yet, so there is nowhere for the data to be written. '
            + 'Add one in the builder.',
        },
  ];

  // EHR launch starts inside the EHR and has no credential-based path, so it keeps its original public-launch rules.
  if (row.action === 'Launch' && row.applicationType === 'EhrLaunch') {
    checks.push({
      label: 'No Segue sign-in needed',
      state: 'note',
      detail: 'Your app does not sign in to Segue on this audience. The run is allowed by this workflow’s “Allow '
        + 'public launch” setting plus the callerId your app holds for that person - so treat callerId as a secret. '
        + 'Signing the clinician in to their EHR is a separate thing and the only sign-in your app is involved in.',
    });
    checks.push(
      row.isPubliclyLaunchable
        ? {
            label: 'Public launch allowed',
            state: 'ok',
            detail: 'A third-party app may mint a launch link for this workflow without a Segue sign-in.',
          }
        : {
            label: 'Public launch allowed',
            state: 'blocked',
            detail: 'Anonymous launch is switched off for this workflow, so the URLs below return “not found”. '
              + 'Turn on “Allow public launch” from this row’s menu.',
          },
      {
        label: 'Your app’s address registered',
        state: 'note',
        detail: 'The anonymous endpoints only accept a callerId whose origin an administrator has added to the '
          + 'allowed list. Send your app’s address (e.g. https://app.example.com) to whoever administers this instance.',
      },
    );
    return checks;
  }

  checks.push(...apiClientChecks(origin));

  if (row.action === 'Launch') {
    // Standalone / Patient: the run page signs the person in, so the workflow must allow public launch.
    checks.push(
      row.isPubliclyLaunchable
        ? {
            label: 'Public launch allowed',
            state: 'ok',
            detail: 'Segue’s run page can take a person through the sign-in for this workflow.',
          }
        : {
            label: 'Public launch allowed',
            state: 'blocked',
            detail: 'The run page needs “Allow public launch” switched on for this workflow, otherwise the sign-in '
              + 'step fails. Turn it on from this row’s menu.',
          },
      {
        label: 'EHR redirect address registered',
        state: 'note',
        detail: 'The hospital’s app registration must list Segue’s callback address, '
          + `${origin}/api/v1/oauth/callback, as a redirect URI, otherwise the EHR refuses the sign-in.`,
      },
    );
  }

  return checks;
}

/**
 * The headers and identity values every caller has to get right, in one place. Three separate things are easy to
 * confuse — and a mix-up shows up only as a silent misbehaviour (someone else's cached sign-in being reused, or a
 * refusal with nothing in the history to look at), so each is named explicitly rather than left to inference.
 * Mirrors exactly what the reference client sends on each call.
 */
function identityAndHeaderValues(interactive: boolean): IntegrationValue[] {
  const values: IntegrationValue[] = interactive
    ? [
      {
        label: 'Authentication — your app does not sign in to Segue',
        value: 'No Authorization header. No Segue sign-in. callerId is the credential.',
        hint: 'Every call below is anonymous as far as Segue is concerned. What authorizes the run is this '
          + 'workflow’s “Allow public launch” setting plus the callerId you send — an unguessable value tied to '
          + 'one person’s stored EHR sign-in. Treat it as a secret: keep it server-side or in this browser only, '
          + 'never in a URL you log or share. If you are wondering where the API key goes, there isn’t one, and '
          + 'that is deliberate rather than a gap.',
        code: true,
      },
      {
        label: 'Cookies — send them only if Segue and your app share a domain',
        value: 'withCredentials: true (harmless either way)   ·   no X-CSRF-Token needed',
        hint: 'The reference client sets withCredentials because it is hosted under the same registrable domain '
          + 'as Segue, so the browser attaches a portal session cookie when the same person happens to have one '
          + 'open. Nothing requires it — the run endpoint is exempt from the CSRF check precisely because its '
          + 'credential is never that cookie, so you do not need to read or echo one. If your app is on an '
          + 'unrelated domain, omit it entirely and everything still works.',
        code: true,
      },
    ]
    : [
      {
        label: 'Sign-in — cookies on every request',
        value: 'Send cookies with the request (withCredentials / credentials: "include")',
        hint: 'Running a workflow on this audience needs a Segue sign-in holding the “workflow.run” permission, '
          + 'and it travels as a cookie, not a bearer token. There is no app-to-app key today, so arrange this '
          + 'with whoever administers this instance before building against it.',
        code: true,
      },
      {
        label: 'Header — X-CSRF-Token (on POST only)',
        value: 'X-CSRF-Token: {value of the fhirbridge_csrf cookie}',
        hint: 'Required on every POST once you are sending a Segue sign-in cookie — read the fhirbridge_csrf '
          + 'cookie and echo its value back in this header. Omitting it is refused as “CSRF token missing or '
          + 'invalid”. GET requests never need it.',
        code: true,
      },
    ];

  values.push({
    label: 'Header — X-Correlation-Id (send it on every call in the attempt)',
    value: 'X-Correlation-Id: {correlationId from validate-run}',
    hint: 'Ties your checks, the sign-in and the run together into ONE entry in Execution History. Without it '
      + 'each leg lands separately and a support question becomes much harder to answer. Take the value '
      + 'validate-run returns and send it on every later call belonging to that attempt — a fresh attempt '
      + 'starts with a fresh validate-run and a new value.',
    code: true,
  });

  if (interactive) {
    values.push({
      label: 'Keep the correlation id — and anything they typed — where the sign-in cannot wipe it',
      value: 'sessionStorage, not variables in your page',
      hint: 'Sending the person to sign in is a full-page navigation, so anything held only in memory is gone by '
        + 'the time they come back. For the correlation id that means the legs after the return land under a '
        + 'different id than the ones before it, which is exactly what this header exists to prevent. The same '
        + 'applies to everything they had already chosen or entered — the site they picked, and any search terms '
        + 'typed before the redirect fired: without them the fetch that runs on the return is not the one they '
        + 'asked for. Store all of it where it survives the round trip (the reference client uses sessionStorage), '
        + 'read it back on return, and carry on where they left off.',
      code: true,
    });
  }

  if (interactive) {
    values.push({
      label: 'The three identity values — do not mix them up',
      value: 'callerId  ·  sessionId  ·  userIdentity',
      hint: 'callerId is your page’s web address, used to send the person back to you, and an administrator must '
        + 'add it to the allowed list. sessionId is an opaque per-browser identifier for the stored sign-in — omit '
        + 'it the first time, save what comes back, and send that same value on every later call (on the run call '
        + 'it is sent as callerId in the body, which is the single most common mistake). userIdentity is your own '
        + 'account identifier for this person, and it is what the access is permanently tied to — so it must be '
        + 'the same value for the same person on every device.',
      code: true,
    });
  }

  return values;
}

/** The credentials, shown once for every API-client based audience. */
function credentialValues(): IntegrationValue[] {
  return [
    {
      label: 'Your credentials - Client ID and Client Secret',
      value: 'clientId  ·  clientSecret   (from Settings > API Clients)',
      hint: 'These identify YOUR APP, not a person, and are the only thing that authorizes a trigger. Keep both on your '
        + 'server: never put the secret in browser code, a page, a URL or a log. If it leaks, an administrator '
        + 'regenerates it in API Clients and the old one stops working immediately. Because the secret stays on your '
        + 'server, the browser only ever carries a short-lived ticket (below), never the secret.',
      code: true,
    },
  ];
}

/** Optional per-run values the launch-ticket call accepts - the same for every audience that can use them. */
function ticketOptionValues(origin: string, includeCriteria: boolean): IntegrationValue[] {
  const values: IntegrationValue[] = [
    {
      label: 'Options you can send with the ticket request',
      value: 'return_url · mode · window_mode · close_on_complete · ehr_endpoint_code'
        + (includeCriteria ? ' · group_id · search_criteria' : ''),
      hint: 'return_url - where the browser is sent when the run finishes; it must be one of your Allowed Caller URLs. '
        + 'mode - “async” (default) shows “Workflow execution started” straight away and returns status=Triggered; '
        + '“sync” waits and returns status=Succeeded or Failed. window_mode - “new” opens Segue’s page in a new window, '
        + '“same” replaces your page. close_on_complete - with a new window, true closes it when done, otherwise it '
        + 'goes to return_url. ehr_endpoint_code - the id of an EHR Endpoint (hospital); see “Hospitals” below.'
        + (includeCriteria
          ? ' group_id / search_criteria - replace the workflow’s saved Group ID / search criteria for this run only '
            + '(e.g. identifier=203713,203711); leave them out to use the saved ones.'
          : ''),
      code: true,
    },
    {
      label: 'Hospitals - list the EHR Endpoint ids you can pass',
      value: `POST ${origin}/api/v1/workflows/external/ehr-endpoints   body: { client_id, client_secret, return_url }`,
      hint: 'You can also copy a single code from Settings > EHR Endpoints (row menu > Copy Code). Or call this from your server (it needs the secret). It returns [{ code, name, vendor }]; send the chosen '
        + 'code as ehr_endpoint_code. return_url must be an Allowed Caller URL, and an unlisted one is refused with '
        + '“caller URL is not whitelisted”. Passing a hospital runs the workflow once against that hospital’s '
        + 'connection values instead of the saved ones; it must be the same EHR vendor as the workflow.',
      code: true,
    },
  ];
  return values;
}

function backendDetails(
  row: WorkflowSummary, origin: string, audience: string,
  values: IntegrationValue[], checks: IntegrationCheck[],
): IntegrationDetails {
  values.push(
    ...credentialValues(),
    {
      label: 'Option A (server to server) - Step 1: get a token',
      value: `POST ${origin}/api/v1/oauth/token   body: { "grant_type": "client_credentials", `
        + '"client_id": "…", "client_secret": "…" }',
      hint: 'Returns { access_token, token_type: "Bearer", expires_in }. The token lasts about 15 minutes and works for '
        + 'any workflow, so cache it and request a new one a little before expires_in. A wrong client or secret '
        + 'returns a generic 401 invalid_client - the same answer for “no such client” and “wrong secret”.',
      code: true,
    },
    {
      label: 'Option A - Step 2: run the workflow',
      value: `POST ${origin}/api/v1/workflows/${row.workflowId}/run   Authorization: Bearer {access_token}`,
      hint: 'Send {"async": true} to start it in the background and get back 202 with { workflowRunId, status: '
        + '"Running", correlationId }, or send {} to wait for the run to finish and receive the full result. '
        + 'Every run gets its own correlation id. A 401 means the token expired or is invalid - get a new one. '
        + 'Per-run hospital / Group ID / search-criteria changes are not accepted here; use Option B for those.',
      code: true,
    },
    {
      label: 'Option A - Step 3: follow an async run',
      value: `GET ${origin}/api/v1/workflows/external/run-status/{workflowRunId}   (no credential needed)`,
      hint: 'Poll every few seconds until status is no longer Running. status is Running, Succeeded, PartialSuccess, '
        + 'Failed or Cancelled. A failed run returns a general errorMessage with a Reference ID you can give to '
        + 'whoever administers this instance; details are not exposed on this unauthenticated call.',
      code: true,
    },
    {
      label: 'Launch-ticket URL (the whole address to POST to)',
      value: `${origin}/api/v1/workflows/external/launch-ticket`,
      hint: 'Used by Option B. POST JSON to this exact address from your server; the body fields are listed below.',
      code: true,
    },
    {
      label: 'Option B (browser redirect) - Step 1: your server asks for a launch ticket',
      value: `POST ${origin}/api/v1/workflows/external/launch-ticket   body: { client_id, client_secret, `
        + `workflow_id: "${row.workflowId}", return_url, … }`,
      hint: 'Call this from your server, never from browser code. Segue checks the credentials, the workflow and that '
        + 'return_url is an Allowed Caller URL, then returns { ticket, run_url, expires_in: 90 }. A refusal is '
        + '401 invalid_client (bad credentials) or 403 caller_url_not_allowed (the return_url is not whitelisted). '
        + 'Use this option when you want the user to watch progress on Segue’s page and be sent back to you, or to '
        + 'run against a specific hospital or with different search criteria.',
      code: true,
    },
    {
      label: 'Option B - Step 2: send the browser to run_url',
      value: 'window.location = run_url   (or window.open for a new window)',
      hint: 'The link works once and for about 90 seconds - use it straight away; a reused or expired link shows “This '
        + 'launch link has expired or was already used”. Segue’s page then starts the workflow and shows '
        + '“Executing workflow…” (or, for async, “Workflow execution started”).',
      code: true,
    },
    {
      label: 'Option B - Step 3: the browser comes back to your return_url',
      value: '?status=Triggered|Succeeded|Failed&workflowRunId={id}',
      hint: 'Triggered means an async run was started; Succeeded / Failed is the outcome of a sync run. Use '
        + 'workflowRunId with the status call from Option A, Step 3, if you need to follow it up.',
      code: true,
    },
    ...ticketOptionValues(origin, true),
    {
      label: 'No sign-in link, and no per-person identity',
      value: 'Do not send callerId, sessionId or userIdentity',
      hint: 'This audience authenticates system-to-system against the EHR, with no person involved, so none of the '
        + 'identity values the interactive audiences use apply here.',
      code: true,
    },
  );

  return {
    workflowId: row.workflowId,
    name: row.name,
    audience,
    kind: 'backend',
    summary: 'This workflow runs unattended. Your server triggers it with an API client’s Client ID and Secret - '
      + 'either by calling the run endpoint with a short-lived token (Option A), or by getting a one-time launch '
      + 'ticket and sending the user’s browser to Segue’s run page (Option B).',
    steps: [
      'An administrator creates an API client and adds your page addresses as Allowed Caller URLs.',
      'Store the Client ID and Secret on your server.',
      'Option A: get a token, call the run endpoint with it, and poll the status.',
      'Option B: ask for a launch ticket from your server, redirect the browser to run_url, and handle the return.',
    ],
    values,
    checks,
  };
}

function ehrLaunchDetails(
  row: WorkflowSummary, origin: string, audience: string,
  values: IntegrationValue[], checks: IntegrationCheck[],
): IntegrationDetails {
  // The URL the hospital registers is the PARTNER'S OWN page, not a Segue URL — Segue is never the first hop of
  // an EHR launch. The EHR opens the partner's page with iss + launch; that page then mints a launch context and
  // forwards the browser to Segue. Handing a hospital a Segue URL to register (as this panel once did) produces a
  // launch that never reaches the partner's app at all.
  values.push(
    {
      label: 'Step 1 — Give the hospital your app’s page address',
      value: 'https://your-app.example.com/your-launch-page',
      hint: 'Replace this with a real page in YOUR app. The hospital registers it in their EHR as the launch '
        + 'URL. When a clinician opens your app from inside the EHR, the EHR opens this page and adds two '
        + 'things to the address: iss (which hospital) and launch (a one-time token). Segue is not the first '
        + 'stop — your page is.',
      code: true,
    },
    {
      label: 'Step 2 — Check the values, and start the attempt',
      value: `POST ${origin}/api/v1/workflows/${row.workflowId}/validate-run`,
      hint: 'Do this before minting the context below. An EHR launch has no click of your own to validate on — '
        + 'the flow starts inside the EHR — so this is the only moment before the round trip begins. Send an '
        + 'empty body ({}); this audience takes its patient from the launch itself, so there is nothing to '
        + 'supply. Keep the correlationId it returns and send it as X-Correlation-Id on Step 3, which bakes it '
        + 'into the launch context so the sign-in and the run land on ONE Execution History entry instead of a '
        + 'validated row plus a separate run. Do this ONLY on the inbound leg (the one carrying iss and launch) — '
        + 'calling it on the return leg mints a second correlation id and strands an orphan entry. If the call '
        + 'itself fails, carry on: it must never be the reason a real EHR launch does not proceed.',
      code: true,
    },
    {
      label: 'What Step 2 sends back',
      value: '{ "isValid": true, "correlationId": "wf-…", "workflowRunId": "…", "errors": [], "parameters": {…} }',
      hint: 'A refusal is a 200 with isValid:false, NOT a 4xx — the request was well-formed and produced a real '
        + 'outcome you can look up. errors is a list of { parameter, message }, each message plain text safe to '
        + 'show a person and carrying no patient data. correlationId comes back either way.',
      code: true,
    },
    {
      label: 'Step 3 — Your page asks Segue for a launch context',
      value: `GET ${origin}/api/v1/workflows/${row.workflowId}/public-launch-context`
        + '?callerId={yourAppUrl}&userIdentity={accountId}',
      hint: 'Call this from your page when it loads. callerId is your own page’s address, and it must be on the '
        + 'allowed list (see below). userIdentity is your own account identifier for the clinician, and the '
        + 'access is permanently tied to it — send the same value for the same person every time, and see the '
        + 'note below on what to do when your page is framed and cannot tell who is using it. You get back a '
        + 'single value called context — a short scrambled string that stands in for this workflow, so real IDs '
        + 'never appear in a browser address bar. Send the correlation id from Step 2 as X-Correlation-Id here: '
        + 'that is what carries it across the redirect, which no header survives.',
      code: true,
    },
    {
      label: 'Step 4 — Send the browser to Segue',
      value: `${origin}/api/v1/oauth/launch/{context}?iss={iss}&launch={launch}&callerId={yourAppUrl}`,
      hint: 'Put the context from Step 3 into the address, and pass straight through the iss and launch values '
        + 'the EHR gave your page in Step 1. This must be a full page redirect, not a background request — the '
        + 'clinician needs to see and complete the hospital’s sign-in screen. If your page runs inside a frame '
        + 'in the EHR, redirect the top-level window, or the sign-in screen will refuse to appear.',
      code: true,
    },
    {
      label: 'Step 5 — Register this address with the hospital too',
      value: `${origin}/api/v1/oauth/callback`,
      hint: 'Where the hospital sends the clinician back once they have signed in. The hospital’s IT team needs '
        + 'this alongside your page address from Step 1. You do not call it yourself.',
      code: true,
    },
    {
      label: 'Step 6 — The clinician lands back on your page',
      value: '?workflowRunId={runId}   ·   ?launchError=workflow_failed   ·   ?launchError=context_mismatch',
      hint: 'Segue runs the workflow and returns the clinician to your page, adding one of these to the address. '
        + 'workflowRunId means it worked and is the reference for Step 7. launchError=workflow_failed means the '
        + 'sign-in worked but the run did not — show a message rather than a blank screen. '
        + 'launchError=context_mismatch is different and needs its own branch: the sign-in itself was rejected '
        + 'because this account is already tied to a different person, so no access was saved and there is '
        + 'nothing to fetch. Consume these once and strip them from the address bar (history.replaceState) — '
        + 'left there, a later refresh re-runs this branch, and a stale run id can show the next person to log '
        + 'in whatever the previous one fetched.',
      code: true,
    },
    {
      label: 'Step 7 — Read what was retrieved',
      value: `GET ${origin}/api/v1/workflows/runs/{workflowRunId}/launch-result?callerId={yourAppUrl}`,
      hint: 'Use the workflowRunId from Step 6. If you did not capture it, '
        + `GET ${origin}/api/v1/workflows/${row.workflowId}/latest-launch-result?callerId={yourAppUrl} `
        + 'returns the most recent run instead — useful when someone reopens your page without a fresh launch, '
        + 'since Segue still holds the data and a refreshable token, so no re-launch is needed. Both need your '
        + 'address on the allowed list. Note the latest-launch-result call is scoped to the workflow, not to a '
        + 'caller: do not rely on it to tell one person’s run from another’s.',
      code: true,
    },
    {
      label: 'Note — one page can serve several EHR vendors',
      value: 'Read iss to decide which workflow to launch',
      hint: 'You register ONE page address, and every hospital on every vendor opens that same page. The iss the '
        + 'EHR sends is what tells them apart — the reference client checks the address it names and picks the '
        + 'matching workflow before Step 3. If you support more than one vendor, do that check first; if you '
        + 'support only one, ignore iss and always use this workflow.',
      code: true,
    },
    {
      label: 'Note — your page may open inside the EHR, not in its own tab',
      value: 'Redirect the top-level window, and expect no cookies',
      hint: 'Some EHRs open your page in a frame inside their own screen. Two things follow. The hospital’s '
        + 'sign-in screen refuses to load in a frame, so Step 4 must redirect the whole browser window, not the '
        + 'frame — and if the browser blocks even that, show a link for the clinician to click instead. Your own '
        + 'app’s cookies are also not sent inside that frame, so your page cannot assume it knows who is using '
        + 'it; the reference client falls back to a fixed identity for userIdentity in that case.',
      code: true,
    },
  );

  values.push(...identityAndHeaderValues(true));

  return {
    workflowId: row.workflowId,
    name: row.name,
    audience,
    kind: 'ehr-launch',
    summary: 'This workflow starts inside the hospital’s EHR. A clinician is already signed in there and opens '
      + 'your app from a menu; the EHR opens a page of yours, your page hands the clinician to Segue to confirm '
      + 'access, and the workflow runs. You cannot start this yourself — there is always a real person clicking.',
    steps: [
      'Give the hospital your app’s launch page address, and Segue’s callback address.',
      'A clinician opens your app from inside the EHR; the EHR opens your page with iss and launch.',
      'Your page checks the values first, then asks Segue for a launch context.',
      'Your page redirects the clinician to Segue with that context.',
      'The clinician signs in at the hospital; Segue runs the workflow and returns them to your page.',
      'Read the result using the run reference that comes back.',
    ],
    values,
    checks,
  };
}

function standaloneDetails(
  row: WorkflowSummary, origin: string, audience: string,
  values: IntegrationValue[], checks: IntegrationCheck[],
): IntegrationDetails {
  const isPatient = row.applicationType === 'Patient';
  const person = isPatient ? 'patient' : 'clinician';

  values.push(
    ...credentialValues(),
    {
      label: 'Why a browser redirect',
      value: 'A person has to sign in at the hospital - the credentials cannot do that for them',
      hint: `This workflow needs the ${person} to sign in and approve access at the EHR, which only they can do. So `
        + 'your server cannot run it with a token alone: it asks Segue for a one-time launch ticket, and Segue’s run '
        + 'page takes the person through the EHR sign-in and consent, then runs the workflow. You build no '
        + 'sign-in screens and handle no EHR tokens.',
      code: true,
    },
    {
      label: 'Launch-ticket URL (the whole address to POST to)',
      value: `${origin}/api/v1/workflows/external/launch-ticket`,
      hint: 'POST JSON to this exact address from your server; the body fields are listed in Step 1.',
      code: true,
    },
    {
      label: 'Step 1 - Your server asks for a launch ticket',
      value: `POST ${origin}/api/v1/workflows/external/launch-ticket   body: { client_id, client_secret, `
        + `workflow_id: "${row.workflowId}", return_url, ehr_endpoint_code, mode, window_mode, close_on_complete }`,
      hint: 'Call this from your server, never from browser code. Segue checks the credentials, the workflow and that '
        + 'return_url is an Allowed Caller URL, then returns { ticket, run_url, expires_in: 90 }. A refusal is '
        + '401 invalid_client (bad credentials) or 403 caller_url_not_allowed (the return_url is not whitelisted).'
        + (isPatient
          ? ' ehr_endpoint_code is required here: a patient always signs in to a specific hospital’s portal.'
          : ' ehr_endpoint_code is optional: leave it out to use the address configured on the workflow.'),
      code: true,
    },
    {
      label: 'Step 2 - Send the browser to run_url',
      value: 'window.location = run_url   (or window.open for a new window)',
      hint: 'The link works once and for about 90 seconds - use it straight away. Segue’s page checks whether the '
        + `${person} is already signed in; if not, it sends them to the hospital’s sign-in and consent screen and `
        + 'brings them back, then runs the workflow. A reused or expired link shows “This launch link has expired or '
        + 'was already used”.',
      code: true,
    },
    {
      label: 'Step 3 - The browser comes back to your return_url',
      value: '?status=Triggered|Succeeded|Failed&workflowRunId={id}',
      hint: 'mode “async” (default) returns status=Triggered as soon as the run starts; mode “sync” waits and returns '
        + 'Succeeded or Failed. If the person cancels the sign-in or it is rejected, Segue’s page shows the reason '
        + 'instead of running. With window_mode “new” and close_on_complete true, the window closes itself instead of '
        + 'returning.',
      code: true,
    },
    {
      label: 'Step 4 (optional) - Follow the run',
      value: `GET ${origin}/api/v1/workflows/external/run-status/{workflowRunId}   (no credential needed)`,
      hint: 'status is Running, Succeeded, PartialSuccess, Failed or Cancelled. A failed run returns a general '
        + 'errorMessage with a Reference ID for whoever administers this instance.',
      code: true,
    },
    ...ticketOptionValues(origin, false).slice(0, 1),
    {
      label: isPatient ? 'Hospitals - list the ids to offer the patient' : 'Hospitals - list the ids you can pass',
      value: `POST ${origin}/api/v1/workflows/external/ehr-endpoints   body: { client_id, client_secret, return_url }`,
      hint: 'Call this from your server. It returns [{ code, name, vendor }] - show the names to your user and send the '
        + 'chosen code as ehr_endpoint_code. return_url must be an Allowed Caller URL. Pick the entries whose '
        + 'vendor matches this workflow.',
      code: true,
    },
    {
      label: 'Note - what this path cannot do',
      value: 'No patient search terms, patient id or Group ID on this path',
      hint: isPatient
        ? 'The patient’s own sign-in decides whose record is read, so there is nothing to narrow.'
        : 'A clinician’s sign-in carries no patient of its own, and the run page does not send search terms. If your '
          + 'workflow needs a patient search term or patient id, build your own screen and use the direct sign-in '
          + 'calls instead (see the next entry).',
      code: true,
    },
    {
      label: 'Alternative - build your own sign-in screens',
      value: `GET ${origin}/api/v1/workflows/${row.workflowId}/token-status · ${isPatient ? 'public-patient-standalone-url' : 'public-standalone-url'} · POST …/run · POST …/discard-token`,
      hint: 'Only for an app that needs full control (for example to pass patient search terms). These calls send the '
        + 'person’s browser session id as callerId and do not use the API client. Ask whoever administers this '
        + 'instance for the detailed sequence before building against it; the ticket flow above is the recommended '
        + 'path.',
      code: true,
    },
  );

  return {
    workflowId: row.workflowId,
    name: row.name,
    audience,
    kind: isPatient ? 'patient' : 'standalone',
    summary: isPatient
      ? 'This workflow needs a patient to sign in once with their own portal account. Your server uses an API client '
        + 'to get a one-time launch ticket and sends the patient’s browser to Segue’s run page, which handles the '
        + 'hospital sign-in and consent, runs the workflow and returns the patient to your app.'
      : 'This workflow needs a clinician to sign in once with their own EHR account. Your server uses an API client '
        + 'to get a one-time launch ticket and sends the clinician’s browser to Segue’s run page, which handles the '
        + 'EHR sign-in and consent, runs the workflow and returns them to your app.',
    steps: [
      'An administrator creates an API client, adds your page addresses as Allowed Caller URLs, and registers '
        + 'Segue’s callback address with the hospital’s app.',
      'Store the Client ID and Secret on your server.',
      `Your server asks Segue for a launch ticket (choosing the hospital for the ${person}).`,
      'Redirect the browser to the run_url you get back.',
      `The ${person} signs in at the hospital on Segue’s page; Segue runs the workflow.`,
      'The browser returns to your page with the outcome.',
    ],
    values,
    checks,
  };
}

/** Renders the panel as plain text, so an admin can paste it straight into an email to the partner. */
export function integrationDetailsAsText(details: IntegrationDetails): string {
  const stateMarker = (state: IntegrationCheck['state']) =>
    state === 'ok' ? 'OK' : state === 'blocked' ? '!!' : '--';

  return [
    `Segue integration details — ${details.name}`,
    `Audience: ${details.audience}`,
    '',
    details.summary,
    '',
    'Steps:',
    ...details.steps.map((step, index) => `  ${index + 1}. ${step}`),
    '',
    'Values:',
    ...details.values.flatMap(value =>
      value.hint ? [`  ${value.label}: ${value.value}`, `      ${value.hint}`] : [`  ${value.label}: ${value.value}`]),
    '',
    'Before this will work:',
    ...details.checks.map(check => `  [${stateMarker(check.state)}] ${check.label} — ${check.detail}`),
  ].join('\n');
}
