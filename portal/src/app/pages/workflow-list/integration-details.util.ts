import { WorkflowSummary } from '../../services/workflow-api.service';

/**
 * Builds the "Integration details" panel's contents for one workflow.
 *
 * This is read by a third-party developer who has no access to this portal and nobody to ask, so it has to be
 * both complete and honest about what will not work. Two things drive its shape:
 *
 * 1. The four audiences are executed in genuinely different ways. Backend is a server-to-server call; the three
 *    interactive audiences CANNOT be started from a server at all — the run happens as a side effect of a real
 *    person completing an EHR or patient sign-in. So for those the deliverable is a URL to send a user to, not
 *    an endpoint to call, and saying otherwise would send a partner down a dead end.
 *
 * 2. Every refusal on the anonymous endpoints is a bare 404 — not opted into public launch, wrong audience,
 *    unrecognised hospital id and "no such workflow" are indistinguishable to the caller (deliberately: telling
 *    them apart would leak whether a given workflow exists). The reason is recorded server-side instead, in
 *    Governance > SMART Launch Logs, searchable by this workflow's id. The readiness checks exist to surface
 *    those conditions BEFORE the partner hits the dead end.
 *
 * Kept as a plain function rather than a component method so the per-audience decisions are directly testable
 * without standing up the component (whose constructor opens a SignalR connection).
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
  const checks = buildChecks(row);

  const values: IntegrationValue[] = [
    { label: 'Workflow ID', value: id, hint: 'Identifies this workflow on every call below.', code: true },
    { label: 'Base address', value: origin, hint: 'The Segue instance your app talks to.', code: true },
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

/**
 * The conditions that otherwise produce a bare 404 with no explanation. Ordered so the two that apply to every
 * audience come first, then the launch-only ones.
 */
function buildChecks(row: WorkflowSummary): IntegrationCheck[] {
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

  // POST /run is AllowAnonymous at the route level and authorizes the two callers differently INSIDE the handler
  // (see WorkflowEndpoints): a cookie-authenticated portal run must satisfy workflow.run plus per-vendor Execute,
  // while a third-party run presents no cookie at all and is gated on the workflow's IsPubliclyLaunchable opt-in
  // plus its unguessable callerId. Telling a launch partner they need a Segue sign-in — as this note once did for
  // every audience — sends them asking for a credential they neither need nor can be given.
  if (row.action !== 'Launch') {
    checks.push({
      label: 'Sign-in required to run',
      state: 'note',
      detail: 'Starting a run on this audience needs a Segue sign-in with the “workflow.run” permission, sent as '
        + 'cookies — there is no app-to-app credential today, so talk to whoever administers this instance before '
        + 'you build against it.',
    });
    return checks;
  }

  checks.push({
    label: 'No Segue sign-in needed',
    state: 'note',
    detail: 'Your app does not sign in to Segue at all on this audience. The run is allowed by this workflow’s '
      + '“Allow public launch” setting plus the callerId your app holds for that person — so treat callerId as a '
      + 'secret, not just an identifier. Signing the person in to their EHR is a separate thing entirely, and it '
      + 'is the only sign-in your app is involved in.',
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
    // Not derivable from the summary row — it depends on the partner's own origin, which only they know.
    {
      label: 'Your app’s address registered',
      state: 'note',
      detail: 'The anonymous endpoints only accept a callerId whose origin an administrator has added to the '
        + 'allowed list. Send your app’s address (e.g. https://app.example.com) to whoever administers this instance.',
    },
  );

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

function backendDetails(
  row: WorkflowSummary, origin: string, audience: string,
  values: IntegrationValue[], checks: IntegrationCheck[],
): IntegrationDetails {
  values.push(
    {
      label: 'Run the workflow',
      value: `POST ${origin}/api/v1/workflows/${row.workflowId}/run`,
      hint: 'Send {"async": true} to start it in the background and get a run id straight back, or omit it to '
        + 'wait for the run to finish.',
      code: true,
    },
    {
      label: 'Check a run',
      value: `GET ${origin}/api/v1/workflow-runs/{runId}/status`,
      hint: 'Poll with the run id returned above until the status is no longer Running.',
      code: true,
    },
    {
      label: 'No sign-in link, and no per-person identity',
      value: 'Do not send callerId, sessionId or userIdentity',
      hint: 'This audience authenticates system-to-system against the EHR, with no person involved — so there is '
        + 'nothing to sign in and none of the identity values the interactive audiences use apply here. If you '
        + 'find yourself wanting to borrow a sign-in from one of those, this is the wrong audience for the job: '
        + 'a sign-in belongs to the person who made it, and reusing it across accounts leaks one person’s access '
        + 'to another.',
      code: true,
    },
  );

  values.push(...identityAndHeaderValues(false));

  return {
    workflowId: row.workflowId,
    name: row.name,
    audience,
    kind: 'backend',
    summary: 'This workflow runs unattended — your system calls it directly, with no person involved.',
    steps: [
      'Sign in to obtain a session.',
      'POST to the run endpoint below with the workflow ID.',
      'Poll the status endpoint with the run id you get back.',
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
  // Provider Standalone and Patient Standalone differ only in which hospital directory they use and which mint
  // endpoint serves them. Passing the wrong endpointType is rejected as a bare 404, so the correct value is
  // shown pre-filled rather than left to guesswork.
  const isPatient = row.applicationType === 'Patient';
  const person = isPatient ? 'patient' : 'clinician';
  const endpointType = isPatient ? 'MyChart' : 'Epic';
  const mintPath = isPatient
    ? `${origin}/api/v1/workflows/${row.workflowId}/public-patient-standalone-url`
    : `${origin}/api/v1/workflows/${row.workflowId}/public-standalone-url`;

  values.push(
    {
      label: 'Step 1 — Check the values, and start the attempt',
      value: `POST ${origin}/api/v1/workflows/${row.workflowId}/validate-run`,
      hint: 'Do this FIRST, before the sign-in check below — a parameter problem is worth reporting without '
        + `first sending the ${person} through a sign-in for a run that was never going to be accepted. Send the `
        + 'same body as the run. It also records the attempt even when refused, so a rejected attempt leaves '
        + 'something to look at afterwards. Keep the correlationId it returns and send it as X-Correlation-Id on '
        + 'every later call in this attempt. If this call itself fails to answer (network blip, older Segue), '
        + 'carry on to the run anyway — a pre-flight must never be the reason a fetch that would have worked '
        + 'does not happen.',
      code: true,
    },
    {
      label: 'What Step 1 sends back',
      value: '{ "isValid": true, "correlationId": "wf-…", "workflowRunId": "…", "errors": [], "parameters": {…} }',
      hint: 'A refusal is a 200 with isValid:false, NOT a 4xx — the request was well-formed and produced a real '
        + 'outcome you can look up, so do not treat it as a transport error. When isValid is false, errors is a '
        + 'list of { parameter, message } — message is plain text written for your end user and carries no '
        + 'patient data, so you can show it as-is. correlationId comes back either way; keep it (see the header '
        + 'below). Show every error at once, not one at a time: each correction the person misses costs them '
        + 'another trip through the sign-in.',
      code: true,
    },
    {
      label: 'Step 2 — Show the list of sites to sign in to',
      value: `GET ${origin}/api/v1/ehr-public-endpoints?endpointType=${endpointType}`,
      hint: isPatient
        ? 'Lists the sites this audience can use — patients sign in to a named hospital’s own portal, so '
          + 'endpointType=MyChart is the only accepted value and any other comes back as “not found”. Add '
          + '&search=name to filter as the patient types. Let them pick one, and keep the id of their choice.'
        : 'Lists the sites this audience can use. Call it once per vendor you support: endpointType=Epic and '
          + 'endpointType=Ecw are both valid here (endpointType=MyChart is not — that is the patient audience). '
          + 'Add &search=name to filter as the clinician types. Let them pick one, and keep the id of their '
          + 'choice.',
      code: true,
    },
    {
      label: 'Step 3 — Check whether a sign-in is even needed',
      value: `GET ${origin}/api/v1/workflows/${row.workflowId}/token-status?callerId={sessionId}`,
      hint: 'Answers hasValidToken: true or false. True means this browser already signed in recently and you '
        + 'can skip straight to Step 6 — do not send them through sign-in again. False means continue to Step 4. '
        + 'Skipping this check still works; it just sends people through a sign-in they did not need. It is a '
        + 'point-in-time answer, not a guarantee: a sign-in can still run out between this check and a later '
        + 'run, which is what Step 8 is for.',
      code: true,
    },
    {
      label: 'Step 4 — Get the sign-in link',
      value: `GET ${mintPath}?ehrEndpointId={id}&callerId={yourPageUrl}&sessionId={sessionId}&userIdentity={accountId}`,
      hint: 'Returns a launchUrl to send the person to. Note the three different values: callerId is your page’s '
        + 'web address (where they come back to, and it must be on the allowed list); sessionId is an opaque '
        + 'identifier for this browser — leave it out the first time and save the one that comes back; '
        + 'userIdentity is your own account identifier for this person, which is what the access is permanently '
        + 'tied to. Mixing these up is the most common mistake here.',
      code: true,
    },
    {
      label: 'Step 5 — Send them to the link to sign in',
      value: '{launchUrl from Step 4}',
      hint: `Redirect the whole page — not a background request. The ${person} signs in at the site they picked `
        + 'and is then returned to your page from Step 4 with one of four markers on the address. '
        + '?workflowRunId=… means a run happened — read it with Step 7, then carry on. ?signedIn=1 means they '
        + 'signed in with nothing to run yet — go straight to Step 6. ?launchError=workflow_failed means the '
        + 'sign-in worked but the run Segue started for you did not — harmless, carry on to Step 6'
        + (isPatient
          ? '. '
          : ', and expect to see it nearly every time on this audience: that run is started before your page '
            + 'reloads, so it has none of the search terms the clinician typed and fails the same “tell me which '
            + 'patients” rule described below. It is not a sign anything is wrong. ')
        + '?launchError=context_mismatch is the one to handle separately: the sign-in itself was rejected '
        + 'because this account is already tied to a different person, so no access was saved and there is '
        + 'nothing to fetch — show the rejection instead of running.',
      code: true,
    },
    {
      label: 'Step 6 — Run the workflow, as often as you like',
      value: `POST ${origin}/api/v1/workflows/${row.workflowId}/run`,
      hint: 'Send {"patientId": …, "patientSearchCriteria": …, "callerId": {sessionId}}'
        + (isPatient ? '. ' : ' — see the note below on which of those two you have to supply. ')
        + 'This is the part worth '
        + 'knowing: once someone has signed in, your app runs the workflow directly and repeatedly — searching, '
        + 'refining, opening a record — without sending them back through sign-in each time, for as long as that '
        + 'sign-in lasts (see Step 8 for when it stops). Your app presents no Segue credential here: the callerId '
        + 'in the body is what identifies the stored EHR sign-in to run against.',
      code: true,
    },
    {
      label: 'What Step 6 sends back',
      value: '{ "workflowRun": { "status": "Succeeded", "errorMessage": null }, "outputsByNodeId": { "{nodeId}": '
        + '{ "nodeType": "…SourceNode", "payload": { "resources": [ { "resourceType": "Patient", '
        + '"resourceId": "…", "payload": "{…}" } ] } } } }',
      hint: 'Check workflowRun.status first — “Succeeded” or not; errorMessage carries the reason when it is not '
        + '(and Step 8 is how you tell one kind of failure from the other). The data sits under outputsByNodeId, '
        + 'keyed by node id: find the source node’s entry (its nodeType ends in SourceNode) and read '
        + 'payload.resources. Two things surprise people here — the node ids differ per workflow, so match on '
        + 'nodeType rather than hard-coding an id; and each resource’s own payload is a STRING of FHIR JSON, so '
        + 'it needs parsing again before you can read fields off it.',
      code: true,
    },
    {
      label: 'Step 7 — Read what a run retrieved',
      value: `GET ${origin}/api/v1/workflows/runs/{workflowRunId}/launch-result?callerId={yourPageUrl}`,
      hint: 'Use this for the run id handed back on the return in Step 5. Runs you started yourself in Step 6 '
        + 'return their results directly, so this is only needed for that first returning leg.',
      code: true,
    },
    {
      label: 'Step 8 — Handle the sign-in running out',
      value: 'errorMessage contains “has no authorized token” or “Re-authorize the source”',
      hint: `A sign-in does not last forever, and it can also be withdrawn at the EHR — so a run that worked `
        + 'earlier can start failing at any point. Check the failed run’s errorMessage for either of those two '
        + `phrases: both mean there is no usable access left and the ${person} has to sign in again. Send them `
        + 'back through Steps 2–5 (start a new attempt with Step 1, so the retry gets its own correlation id), '
        + 'then run again. Any OTHER error message is a normal failure — show it and let them retry, and do not '
        + 'send them through a sign-in they do not need. The same check applies to an error thrown by the run '
        + 'call itself, not just a failed run in the response.',
      code: true,
    },
    ...(isPatient ? [] : [{
      label: 'Note — this audience must be told which patients to fetch',
      value: 'patientSearchCriteria: "family=Smith"   ·   or   ·   patientId: "…"',
      hint: 'A clinician signs in as themselves, so unlike the patient audience the sign-in carries no patient of '
        + 'its own — nothing narrows the fetch unless you narrow it. Send either patientSearchCriteria (FHIR '
        + 'search terms, e.g. family=Smith or identifier=MRN12345) or a patientId on both Step 1 and Step 6. '
        + 'Send neither and Step 1 refuses the attempt, which is the point of calling it first: Epic would '
        + 'otherwise reject the run with business rule 59108 (“A patient is required”), or hand back an empty '
        + 'bundle, only after the clinician had already been through a full sign-in.',
      code: true,
    }]),
    {
      label: isPatient
        ? 'Note — every patient sign-in needs a site id'
        : 'Note — some sites have no list to choose from',
      value: isPatient
        ? 'ehrEndpointId is required'
        : 'ehrEndpointId is optional — omit it to use the workflow’s own configured address',
      hint: isPatient
        ? 'A patient always signs in to a specific hospital’s portal, so Step 2 is never skippable and the id '
          + 'from it must be sent in Step 4.'
        : 'Step 2 only applies when a clinician has a choice to make. A workflow pointed at a single practice '
          + '(one fixed address, nothing to pick) skips Step 2 entirely and omits ehrEndpointId in Step 4 — '
          + 'Segue then uses the address configured on the workflow itself. Build the picker only for the '
          + 'vendors that actually have a directory.',
      code: true,
    },
    {
      label: 'Optional — Sign out',
      value: `POST ${origin}/api/v1/workflows/${row.workflowId}/discard-token?callerId={sessionId}`,
      hint: 'Forgets the stored sign-in so the next run asks for a fresh one — use it for a “sign out” button. '
        + 'It does not sign the person out of the EHR itself, so if they still have a session there, the next '
        + 'sign-in may not visibly prompt them again.',
      code: true,
    },
  );

  values.push(...identityAndHeaderValues(true));

  return {
    workflowId: row.workflowId,
    name: row.name,
    audience,
    kind: isPatient ? 'patient' : 'standalone',
    summary: isPatient
      ? 'This workflow needs a patient to sign in once with their own portal account. After that your app can '
        + 'run it on demand for as long as that sign-in lasts — the sign-in is the one thing you cannot do for '
        + 'them.'
      : 'This workflow needs a clinician to sign in once with their own EHR account. After that your app can run '
        + 'it on demand for as long as that sign-in lasts — the sign-in is the one thing you cannot do for them.',
    steps: [
      'Check the values first, and keep the correlation id it gives you.',
      'Show the list of sites and let your user pick one.',
      'Check whether they are already signed in — if so, skip ahead and just run it.',
      'Otherwise get a sign-in link and send them to it.',
      'They sign in and come back to your page.',
      'Run the workflow as often as you need, and read the results.',
      'When the sign-in eventually runs out, send them back through it and carry on.',
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
