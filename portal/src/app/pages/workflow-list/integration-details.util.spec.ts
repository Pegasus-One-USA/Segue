import { WorkflowSummary } from '../../services/workflow-api.service';
import { buildIntegrationDetails, integrationDetailsAsText } from './integration-details.util';

/**
 * A partner developer acts on this panel unsupervised. It is organised around API clients (Client ID + Secret kept
 * on the partner's server): Backend workflows can be triggered with a token or through a launch ticket, the
 * interactive audiences only through a launch ticket (Segue's run page does the EHR sign-in), and EHR Launch — which
 * starts inside the EHR — keeps its own launch sequence.
 */
describe('buildIntegrationDetails', () => {
  const ORIGIN = 'https://segue.example.com';
  const WORKFLOW_ID = '11111111-2222-3333-4444-555555555555';

  function row(overrides: Partial<WorkflowSummary> = {}): WorkflowSummary {
    return {
      workflowId: WORKFLOW_ID,
      name: 'Epic to Warehouse',
      status: 'Ready',
      nodes: 3,
      edges: 2,
      lastRun: null,
      lastRunAt: null,
      action: 'Run',
      actionEndpoint: '/api/v1/workflows/x/run',
      sourceConnectionId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
      sourceSystemType: 'Epic',
      applicationType: 'Backend',
      hasDestination: true,
      isPubliclyLaunchable: true,
      ...overrides,
    } as WorkflowSummary;
  }

  function valuesOf(details: { values: { value: string }[] }): string {
    return details.values.map(v => v.value).join('\n');
  }

  function hintsOf(details: { values: { hint?: string }[] }): string {
    return details.values.map(v => v.hint ?? '').join('\n');
  }

  function textOf(details: { values: { value: string; hint?: string }[] }): string {
    return valuesOf(details) + '\n' + hintsOf(details);
  }

  const INTERACTIVE: [string, Partial<WorkflowSummary>][] = [
    ['Provider Standalone', { action: 'Launch', applicationType: 'Standalone' }],
    ['Patient Standalone', { action: 'Launch', applicationType: 'Patient' }],
  ];

  describe('Backend', () => {
    it('offers both ways to trigger with an API client: token + run, and launch ticket', () => {
      const details = buildIntegrationDetails(row(), ORIGIN, 'Backend Service');
      const values = valuesOf(details);

      expect(details.kind).toBe('backend');
      expect(values).toContain(`POST ${ORIGIN}/api/v1/oauth/token`);
      expect(values).toContain('client_credentials');
      expect(values).toContain(`POST ${ORIGIN}/api/v1/workflows/${WORKFLOW_ID}/run`);
      expect(values).toContain('Authorization: Bearer');
      expect(values).toContain(`${ORIGIN}/api/v1/workflows/external/launch-ticket`);
      expect(values).toContain(`workflow_id: "${WORKFLOW_ID}"`);
    });

    it('tells the partner to keep the secret on the server', () => {
      const details = buildIntegrationDetails(row(), ORIGIN, 'Backend Service');

      expect(details.values.some(v => v.label.startsWith('Your credentials'))).toBeTrue();
      expect(hintsOf(details)).toContain('Keep both on your server');
      expect(hintsOf(details)).toContain('never from browser code');
    });

    it('explains how to follow an async run without a credential', () => {
      const text = textOf(buildIntegrationDetails(row(), ORIGIN, 'Backend Service'));

      expect(text).toContain('/api/v1/workflows/external/run-status/{workflowRunId}');
      expect(text).toContain('Running, Succeeded, PartialSuccess, Failed or Cancelled');
    });

    it('documents the launch-ticket options, including per-run Group ID and search criteria', () => {
      const text = textOf(buildIntegrationDetails(row(), ORIGIN, 'Backend Service'));

      for (const option of ['return_url', 'window_mode', 'close_on_complete', 'ehr_endpoint_code', 'group_id', 'search_criteria']) {
        expect(text).toContain(option);
      }
      expect(text).toContain('/api/v1/workflows/external/ehr-endpoints');
      expect(text).toContain('status=Triggered');
    });

    it('no longer asks for a portal sign-in, cookies or a CSRF header', () => {
      const details = buildIntegrationDetails(row(), ORIGIN, 'Backend Service');
      const text = textOf(details);

      expect(details.checks.some(c => c.label === 'Sign-in required to run')).toBeFalse();
      expect(text).not.toContain('X-CSRF-Token');
      expect(text).not.toContain('withCredentials');
    });

    it('does not offer a public launch URL — this audience has no user to send anywhere', () => {
      const values = valuesOf(buildIntegrationDetails(row(), ORIGIN, 'Backend Service'));

      expect(values).not.toContain('public-standalone-url');
      expect(values).not.toContain('ehr-public-endpoints');
    });

    it('tells Backend not to use the interactive identity values at all', () => {
      expect(valuesOf(buildIntegrationDetails(row(), ORIGIN, 'Backend Service')))
        .toContain('Do not send callerId, sessionId or userIdentity');
    });
  });

  describe('Provider and Patient Standalone', () => {
    it('route everything through the launch ticket and the run page, not the run endpoint', () => {
      for (const [label, overrides] of INTERACTIVE) {
        const details = buildIntegrationDetails(row(overrides), ORIGIN, label);
        const values = valuesOf(details);

        expect(values).toContain(`${ORIGIN}/api/v1/workflows/external/launch-ticket`);
        expect(values).toContain('run_url');
        expect(values).toContain('?status=Triggered|Succeeded|Failed&workflowRunId={id}');
        expect(values).not.toMatch(/POST [^\n]*\/workflows\/[^/\s]+\/run\b/);
      }
    });

    it('explains why a credential alone cannot run it, and that the person signs in on Segue’s page', () => {
      for (const [label, overrides] of INTERACTIVE) {
        const text = textOf(buildIntegrationDetails(row(overrides), ORIGIN, label));

        expect(text).toContain('A person has to sign in at the hospital');
        expect(text).toContain('You build no sign-in screens');
      }
    });

    it('says the launch link is single use and short-lived', () => {
      for (const [label, overrides] of INTERACTIVE) {
        const hints = hintsOf(buildIntegrationDetails(row(overrides), ORIGIN, label));

        expect(hints).toContain('works once and for about 90 seconds');
        expect(hints).toContain('This launch link has expired or was already used');
      }
    });

    it('explains the 401 / 403 refusals, including the not-whitelisted caller URL', () => {
      for (const [label, overrides] of INTERACTIVE) {
        const hints = hintsOf(buildIntegrationDetails(row(overrides), ORIGIN, label));

        expect(hints).toContain('401 invalid_client');
        expect(hints).toContain('403 caller_url_not_allowed');
      }
    });

    it('requires a hospital for the patient audience and makes it optional for the provider audience', () => {
      const patient = hintsOf(buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Patient' }), ORIGIN, 'Patient Standalone'));
      const provider = hintsOf(buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Standalone' }), ORIGIN, 'Provider Standalone'));

      expect(patient).toContain('ehr_endpoint_code is required');
      expect(provider).toContain('ehr_endpoint_code is optional');
    });

    it('lists hospitals through the credential-protected endpoint', () => {
      for (const [label, overrides] of INTERACTIVE) {
        const values = valuesOf(buildIntegrationDetails(row(overrides), ORIGIN, label));

        expect(values).toContain(`POST ${ORIGIN}/api/v1/workflows/external/ehr-endpoints`);
      }
    });

    it('is honest that search terms and Group ID are not available on this path', () => {
      for (const [label, overrides] of INTERACTIVE) {
        const details = buildIntegrationDetails(row(overrides), ORIGIN, label);

        expect(valuesOf(details)).toContain('No patient search terms, patient id or Group ID on this path');
        expect(valuesOf(details)).not.toContain('group_id');
      }
    });

    it('points at the old direct calls only as an advanced alternative, with the right mint endpoint', () => {
      const provider = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Standalone' }), ORIGIN, 'Provider Standalone');
      const patient = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Patient' }), ORIGIN, 'Patient Standalone');

      expect(provider.kind).toBe('standalone');
      expect(valuesOf(provider)).toContain('public-standalone-url');
      expect(patient.kind).toBe('patient');
      expect(valuesOf(patient)).toContain('public-patient-standalone-url');
      expect(valuesOf(patient)).not.toContain('endpointType=Epic');
    });
  });

  describe('EHR Launch', () => {
    function ehr() {
      return buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'EhrLaunch' }), ORIGIN, 'EHR Launch (Provider)');
    }

    it('keeps its own launch sequence — no credential can start an EHR launch', () => {
      const details = ehr();

      expect(details.kind).toBe('ehr-launch');
      expect(valuesOf(details)).toContain('/public-launch-context');
      expect(valuesOf(details)).toContain('/api/v1/oauth/launch/{context}');
      expect(valuesOf(details)).toContain(`${ORIGIN}/api/v1/oauth/callback`);
      expect(valuesOf(details)).not.toContain('launch-ticket');
      expect(details.checks.some(c => c.label === 'No Segue sign-in needed')).toBeTrue();
    });

    it('never hands EHR Launch the retired per-source-connection entry point', () => {
      expect(valuesOf(ehr())).not.toContain('/source-connections/');
    });

    it('documents validate-run before the launch context, and both launchError values', () => {
      const values = valuesOf(ehr());
      const text = textOf(ehr());

      expect(values.indexOf('/validate-run')).toBeLessThan(values.indexOf('/public-launch-context'));
      expect(text).toContain('launchError=workflow_failed');
      expect(text).toContain('launchError=context_mismatch');
      expect(text).toContain('replaceState');
    });

    it('tells EHR Launch how to read the result both with and without a run id', () => {
      const text = textOf(ehr());

      expect(text).toContain('/launch-result');
      expect(text).toContain('/latest-launch-result');
    });

    it('tells EHR Launch to route on iss and to cope with being framed', () => {
      expect(valuesOf(ehr())).toContain('Read iss to decide which workflow to launch');
      expect(valuesOf(ehr())).toContain('Redirect the top-level window');
    });

    it('still explains the identity values and the correlation header for EHR Launch', () => {
      expect(valuesOf(ehr())).toContain('callerId  ·  sessionId  ·  userIdentity');
      expect(valuesOf(ehr())).toContain('X-Correlation-Id');
    });
  });

  describe('Launch-ticket URL', () => {
    it('is a separate, copyable value holding only the address, for Backend and the interactive audiences', () => {
      for (const overrides of [{}, ...INTERACTIVE.map(([, o]) => o)]) {
        const details = buildIntegrationDetails(row(overrides), ORIGIN, 'x');
        const entry = details.values.find(v => v.label.startsWith('Launch-ticket URL'));

        expect(entry?.value).toBe(`${ORIGIN}/api/v1/workflows/external/launch-ticket`);
      }
    });

    it('is not offered to EHR Launch, which no credential can start', () => {
      const details = buildIntegrationDetails(row({ action: 'Launch', applicationType: 'EhrLaunch' }), ORIGIN, 'x');

      expect(details.values.some(v => v.label.startsWith('Launch-ticket URL'))).toBeFalse();
    });
  });

  describe('Readiness checks', () => {
    it('lists the API-client setup for the credential-based audiences, and not for EHR Launch', () => {
      for (const overrides of [{}, ...INTERACTIVE.map(([, o]) => o)]) {
        const labels = buildIntegrationDetails(row(overrides), ORIGIN, 'x').checks.map(c => c.label);

        expect(labels).toContain('API client created');
        expect(labels).toContain('Your app’s pages registered as Allowed Caller URLs');
      }

      const ehr = buildIntegrationDetails(row({ action: 'Launch', applicationType: 'EhrLaunch' }), ORIGIN, 'EHR Launch');
      expect(ehr.checks.map(c => c.label)).not.toContain('API client created');
    });

    it('flags public launch being off for the interactive audiences, which breaks the run page’s sign-in', () => {
      const details = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Standalone', isPubliclyLaunchable: false }),
        ORIGIN, 'Provider Standalone');

      expect(details.checks.find(c => c.label === 'Public launch allowed')?.state).toBe('blocked');
    });

    it('does not require public launch for a Backend workflow', () => {
      const labels = buildIntegrationDetails(row({ isPubliclyLaunchable: false }), ORIGIN, 'Backend Service')
        .checks.map(c => c.label);

      expect(labels).not.toContain('Public launch allowed');
    });

    it('tells the interactive audiences to register Segue’s callback address with the hospital', () => {
      const details = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Patient' }), ORIGIN, 'Patient Standalone');

      expect(details.checks.find(c => c.label === 'EHR redirect address registered')?.detail)
        .toContain(`${ORIGIN}/api/v1/oauth/callback`);
    });

    it('flags a missing destination and a disabled workflow', () => {
      const details = buildIntegrationDetails(
        row({ status: 'Disabled', hasDestination: false }), ORIGIN, 'Backend Service');

      expect(details.checks.find(c => c.label === 'Workflow enabled')?.state).toBe('blocked');
      expect(details.checks.find(c => c.label === 'Destination configured')?.state).toBe('blocked');
    });

    it('reports a fully configured workflow as ready', () => {
      for (const overrides of [{}, ...INTERACTIVE.map(([, o]) => o)]) {
        const details = buildIntegrationDetails(row(overrides), ORIGIN, 'x');

        expect(details.checks.filter(c => c.state === 'blocked')).toEqual([]);
      }
    });
  });

  describe('Panel structure', () => {
    const AUDIENCES: [string, Partial<WorkflowSummary>][] = [
      ['Backend Service', {}],
      ...INTERACTIVE,
      ['EHR Launch', { action: 'Launch', applicationType: 'EhrLaunch' }],
    ];

    // Each value gets its own Copy button and the template tracks by label, so duplicates would both break
    // rendering and hand the partner an unusable copy.
    it('gives every value a unique, individually copyable label', () => {
      for (const [label, overrides] of AUDIENCES) {
        const details = buildIntegrationDetails(row(overrides), ORIGIN, label);
        const labels = details.values.map(value => value.label);

        expect(new Set(labels).size).toBe(labels.length);
        expect(details.values.every(value => !value.value.includes('\n'))).toBeTrue();
      }
    });

    it('never prints a real secret or token, only placeholders', () => {
      for (const [label, overrides] of AUDIENCES) {
        const text = textOf(buildIntegrationDetails(row(overrides), ORIGIN, label));

        expect(text).not.toMatch(/eyJ[A-Za-z0-9_-]{10,}/);
        expect(text).not.toMatch(/client_secret\s*[:=]\s*"[^"]{8,}"/);
      }
    });
  });

  describe('Copy as text', () => {
    it('carries the values and the blocking items into the pasted version', () => {
      const details = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Patient', isPubliclyLaunchable: false }),
        ORIGIN, 'Patient Standalone');

      const text = integrationDetailsAsText(details);

      expect(text).toContain('Patient Standalone');
      expect(text).toContain('launch-ticket');
      // Blocking items must survive the copy — an emailed panel that hides them would mislead the partner.
      expect(text).toContain('[!!] Public launch allowed');
    });
  });
});
