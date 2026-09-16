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

  if (row.action !== 'Launch') {
    // No app-to-app credential exists for the run endpoint, so this is a real planning constraint rather than a
    // step the partner can complete on their own — advisory, not blocking.
    checks.push({
      label: 'Sign-in required',
      state: 'note',
      detail: 'This call needs a Segue sign-in with the “workflow.run” permission — there is no app-to-app '
        + 'credential today, so talk to whoever administers this instance before you build against it.',
    });
    return checks;
  }

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
  );

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
  values.push(
    {
      label: 'Launch URL (register with the EHR)',
      value: `${origin}/api/v1/source-connections/{sourceConnectionId}/oauth/launch`,
      hint: 'The EHR calls this, appending its own iss and launch parameters. Give it to the hospital’s IT team.',
      code: true,
    },
    {
      label: 'Redirect URI (register with the EHR)',
      value: `${origin}/api/v1/oauth/callback`,
      hint: 'Where the EHR returns the user once they have signed in.',
      code: true,
    },
  );

  if (row.sourceConnectionId) {
    values.push({
      label: 'Source connection ID',
      value: row.sourceConnectionId,
      hint: 'Substitute this into the launch URL above.',
      code: true,
    });
  }

  return {
    workflowId: row.workflowId,
    name: row.name,
    audience,
    kind: 'ehr-launch',
    summary: 'This workflow starts from inside the EHR. A clinician opens your app from their EHR session, and '
      + 'the workflow runs once that hand-off completes — your app does not start it.',
    steps: [
      'Give the two URLs below to the hospital’s IT team to register.',
      'The clinician opens your app from within the EHR.',
      'Read the result using the run id returned to your redirect URI.',
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
  const endpointType = isPatient ? 'MyChart' : 'Epic';
  const mintPath = isPatient
    ? `${origin}/api/v1/workflows/${row.workflowId}/public-patient-standalone-url`
    : `${origin}/api/v1/workflows/${row.workflowId}/public-standalone-url`;

  values.push(
    {
      label: 'Hospital list',
      value: `GET ${origin}/api/v1/ehr-public-endpoints?endpointType=${endpointType}`,
      hint: `Lists the sites this audience can launch against. Use endpointType=${endpointType} — any other `
        + 'value is rejected as “not found”.',
      code: true,
    },
    {
      label: 'Get the launch link',
      value: `GET ${mintPath}?ehrEndpointId={id}&callerId={yourAppUrl}`,
      hint: 'Call this after your user picks a site. callerId must be your app’s address, registered on the '
        + 'allowed list. Returns a launchUrl to send the user to.',
      code: true,
    },
    {
      label: 'Read the result',
      value: `GET ${origin}/api/v1/workflows/runs/{workflowRunId}/launch-result?callerId={yourAppUrl}`,
      hint: 'Once the user has signed in, fetch what the run retrieved. Your app’s address must be registered.',
      code: true,
    },
  );

  return {
    workflowId: row.workflowId,
    name: row.name,
    audience,
    kind: isPatient ? 'patient' : 'standalone',
    summary: isPatient
      ? 'This workflow runs when a patient signs in with their own portal credentials. Your app sends them to a '
        + 'link — it cannot start the run on their behalf.'
      : 'This workflow runs when a clinician signs in directly. Your app sends them to a link — it cannot start '
        + 'the run on their behalf.',
    steps: [
      'Fetch the hospital list and let your user pick one.',
      'Request a launch link for that choice.',
      `Send the ${isPatient ? 'patient' : 'clinician'} to the link to sign in.`,
      'Read the result once they return.',
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
