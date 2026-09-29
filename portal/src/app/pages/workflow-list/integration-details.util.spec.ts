import { WorkflowSummary } from '../../services/workflow-api.service';
import { buildIntegrationDetails, integrationDetailsAsText } from './integration-details.util';

/**
 * A partner developer acts on this panel unsupervised, so the per-audience decisions have to be right: pointing
 * an interactive audience at the run endpoint, or naming the wrong hospital directory, sends them down a path
 * that fails as an unexplained 404.
 */
describe('buildIntegrationDetails', () => {
  const ORIGIN = 'https://segue.example.com';

  function row(overrides: Partial<WorkflowSummary> = {}): WorkflowSummary {
    return {
      workflowId: '11111111-2222-3333-4444-555555555555',
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

  describe('Backend', () => {
    it('gives the run endpoint, because a server can start this audience directly', () => {
      const details = buildIntegrationDetails(row(), ORIGIN, 'Backend Service');

      expect(details.kind).toBe('backend');
      expect(valuesOf(details)).toContain(
        `POST ${ORIGIN}/api/v1/workflows/11111111-2222-3333-4444-555555555555/run`);
    });

    it('warns that there is no app-to-app credential, so a partner plans for it up front', () => {
      const details = buildIntegrationDetails(row(), ORIGIN, 'Backend Service');

      const signIn = details.checks.find(check => check.label === 'Sign-in required');
      expect(signIn?.state).toBe('note');
      expect(signIn?.detail).toContain('no app-to-app');
    });

    it('does not offer a public launch URL — this audience has no user to send anywhere', () => {
      const details = buildIntegrationDetails(row(), ORIGIN, 'Backend Service');

      expect(valuesOf(details)).not.toContain('public-standalone-url');
      expect(valuesOf(details)).not.toContain('ehr-public-endpoints');
    });
  });

  describe('Interactive audiences', () => {
    // Matches the run endpoint specifically rather than the bare substring '/run': every interactive audience
    // legitimately cites /workflows/runs/{id}/launch-result to read what a completed launch retrieved.
    it('never offers the run endpoint — these cannot be started from a server', () => {
      for (const applicationType of ['Standalone', 'Patient', 'EhrLaunch']) {
        const details = buildIntegrationDetails(
          row({ action: 'Launch', applicationType }), ORIGIN, applicationType);

        expect(valuesOf(details)).not.toMatch(/\/workflows\/[^/\s]+\/run\b/);
        expect(valuesOf(details)).not.toContain('POST');
      }
    });

    it('sends Provider Standalone to the Epic directory and its own mint endpoint', () => {
      const details = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Standalone' }), ORIGIN, 'Provider Standalone');

      expect(details.kind).toBe('standalone');
      expect(valuesOf(details)).toContain('endpointType=Epic');
      expect(valuesOf(details)).toContain('/public-standalone-url');
    });

    // The panel long claimed a standalone partner "cannot start the run on their behalf". The reference client
    // (Demo_TestApp's Provider Standalone page) shows otherwise: the sign-in is one-time, and afterwards the app
    // calls /run directly and repeatedly against the cached token. Omitting that made the audience look far more
    // restrictive than it is, and left token-status/validate-run/discard-token entirely undocumented.
    it('documents the standalone run-after-sign-in cycle, not just the sign-in', () => {
      for (const applicationType of ['Standalone', 'Patient']) {
        const details = buildIntegrationDetails(
          row({ action: 'Launch', applicationType }), ORIGIN, applicationType);
        const values = valuesOf(details);

        expect(values).toContain('/token-status');
        expect(values).toContain('/validate-run');
        expect(values).toContain('/discard-token');
        expect(values).toMatch(/\/workflows\/[^/\s]+\/run\b/);
      }
    });

    // Ordering, not just presence: validate-run listed after the sign-in steps reads as an afterthought, and a
    // partner following the panel top-to-bottom then bounces someone through an EHR sign-in for a run that was
    // never going to be accepted — and has no correlation id to send on the legs before it. The reference client
    // (Demo_TestApp) and WorkflowEndpoints' own contract both put it first.
    it('puts validate-run before the sign-in check on the interactive audiences', () => {
      for (const applicationType of ['Standalone', 'Patient']) {
        const values = valuesOf(buildIntegrationDetails(
          row({ action: 'Launch', applicationType }), ORIGIN, applicationType));

        expect(values.indexOf('/validate-run')).toBeGreaterThanOrEqual(0);
        expect(values.indexOf('/validate-run')).toBeLessThan(values.indexOf('/token-status'));
      }
    });

    // A correlation id held in a page variable is gone by the time the full-page sign-in returns, so the legs
    // after the return land under a different id than the ones before it — silently defeating the header. The
    // same wipe takes the site they picked and anything they typed, so the fetch that runs on the return is not
    // the one they asked for — the reference client persists all three.
    it('warns that the correlation id and in-flight input must survive the sign-in round trip', () => {
      for (const applicationType of ['Standalone', 'Patient']) {
        const hints = hintsOf(buildIntegrationDetails(
          row({ action: 'Launch', applicationType }), ORIGIN, applicationType));

        expect(hints).toContain('sessionStorage');
        expect(hints).toContain('search terms');
      }
    });

    // Without the response shapes a partner reverse-engineers them from Swagger or guesses. Two details cost real
    // debugging time: a refusal is a 200 with isValid:false (treating it as a transport error discards the run id
    // and the errors), and each resource's own payload is a JSON STRING needing a second parse.
    it('shows what validate-run and run send back', () => {
      for (const applicationType of ['Standalone', 'Patient']) {
        const details = buildIntegrationDetails(
          row({ action: 'Launch', applicationType }), ORIGIN, applicationType);
        const text = valuesOf(details) + '\n' + hintsOf(details);

        expect(text).toContain('isValid');
        expect(text).toContain('outputsByNodeId');
        expect(text).toContain('payload.resources');
        expect(text).toContain('workflowRun.status');
      }
    });

    // PatientScopeRequiredRule refuses a Provider Standalone attempt carrying neither patientSearchCriteria nor
    // patientId — a clinician's sign-in establishes no patient of its own. Undocumented, a partner discovers it as
    // Epic business rule 59108 or an empty bundle, and only after putting someone through a full sign-in. The
    // patient audience has the opposite situation (the token carries the patient), so the note must NOT appear
    // there, where it would just be wrong.
    it('tells the provider audience it must supply search criteria or a patient id', () => {
      const provider = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Standalone' }), ORIGIN, 'Provider Standalone');
      const providerText = valuesOf(provider) + '\n' + hintsOf(provider);

      expect(providerText).toContain('patientSearchCriteria');
      expect(providerText).toContain('59108');

      const patient = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Patient' }), ORIGIN, 'Patient Standalone');
      expect(hintsOf(patient)).not.toContain('59108');
    });

    // A sign-in expires (or is withdrawn at the EHR), so a run that worked earlier starts failing. Without these
    // two phrases a partner cannot tell "send them back through sign-in" from "show the error and let them
    // retry" — and gets it wrong in one direction or the other. Both come from SmartAuthorizationCodeTokenProvider.
    it('tells the partner how to recognise an expired sign-in', () => {
      for (const applicationType of ['Standalone', 'Patient']) {
        const details = buildIntegrationDetails(
          row({ action: 'Launch', applicationType }), ORIGIN, applicationType);
        const text = valuesOf(details) + '\n' + hintsOf(details);

        expect(text).toContain('has no authorized token');
        expect(text).toContain('Re-authorize the source');
      }
    });

    // Handling only workflowRunId/signedIn leaves the two launchError markers to be discovered in production —
    // and context_mismatch in particular means no token was saved, so proceeding to a run is wrong.
    it('documents all four return markers, including both launchError values', () => {
      for (const applicationType of ['Standalone', 'Patient']) {
        const hints = hintsOf(buildIntegrationDetails(
          row({ action: 'Launch', applicationType }), ORIGIN, applicationType));

        expect(hints).toContain('workflowRunId');
        expect(hints).toContain('signedIn=1');
        expect(hints).toContain('launchError=workflow_failed');
        expect(hints).toContain('launchError=context_mismatch');
      }
    });

    // Passing endpointType=Epic for a Patient workflow is refused as a bare 404, so this is exactly the mistake
    // the panel exists to prevent.
    it('sends Patient Standalone to the MyChart directory and the patient mint endpoint', () => {
      const details = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Patient' }), ORIGIN, 'Patient Standalone');

      expect(details.kind).toBe('patient');
      expect(valuesOf(details)).toContain('endpointType=MyChart');
      expect(valuesOf(details)).toContain('/public-patient-standalone-url');
      expect(valuesOf(details)).not.toContain('endpointType=Epic');
    });

    it('walks EHR Launch through the mint-then-redirect handoff, not a link to open', () => {
      const details = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'EhrLaunch' }), ORIGIN, 'EHR Launch (Provider)');

      expect(details.kind).toBe('ehr-launch');
      expect(valuesOf(details)).toContain('/public-launch-context');
      expect(valuesOf(details)).toContain('/api/v1/oauth/launch/{context}');
      expect(valuesOf(details)).toContain(`${ORIGIN}/api/v1/oauth/callback`);
      expect(valuesOf(details)).not.toContain('public-standalone-url');
    });

    // The launch URL a hospital registers is the PARTNER'S page, never a Segue URL, and the workflow is named by
    // an encrypted context rather than a source connection id. Offering the old per-source-connection entry sent
    // hospitals to a URL that never reaches the partner's app.
    it('never hands EHR Launch the retired per-source-connection entry point', () => {
      const details = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'EhrLaunch' }), ORIGIN, 'EHR Launch (Provider)');

      expect(valuesOf(details)).not.toContain('/source-connections/');
      expect(details.values.some(value => value.label === 'Source connection ID')).toBeFalse();
    });

    it('tells EHR Launch how to read the result both with and without a run id', () => {
      const details = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'EhrLaunch' }), ORIGIN, 'EHR Launch (Provider)');

      // The fallback is offered in the hint rather than as its own value, so search both.
      const everything = details.values.map(v => `${v.value}
${v.hint ?? ''}`).join('\n');
      expect(everything).toContain('/launch-result');
      expect(everything).toContain('/latest-launch-result');
    });

    it('tells the partner their own address must be registered', () => {
      const details = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Standalone' }), ORIGIN, 'Provider Standalone');

      expect(details.checks.some(check => check.label === 'Your app’s address registered')).toBeTrue();
    });
  });

  describe('Readiness checks', () => {
    it('flags public launch being off, which otherwise surfaces only as an unexplained 404', () => {
      const details = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Standalone', isPubliclyLaunchable: false }),
        ORIGIN, 'Provider Standalone');

      const check = details.checks.find(c => c.label === 'Public launch allowed');
      expect(check?.state).toBe('blocked');
    });

    it('flags a missing destination and a disabled workflow', () => {
      const details = buildIntegrationDetails(
        row({ status: 'Disabled', hasDestination: false }), ORIGIN, 'Backend Service');

      expect(details.checks.find(c => c.label === 'Workflow enabled')?.state).toBe('blocked');
      expect(details.checks.find(c => c.label === 'Destination configured')?.state).toBe('blocked');
    });

    it('reports a fully configured workflow as ready', () => {
      const details = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Standalone' }), ORIGIN, 'Provider Standalone');

      expect(details.checks.filter(c => c.state === 'blocked')).toEqual([]);
    });
  });

  // A partner gets none of this from the endpoint URLs alone, and each omission fails silently rather than
  // loudly: no cookies is a refusal, no CSRF header is a confusing 403, and the wrong identity value quietly
  // reuses somebody else's cached sign-in.
  // Per-vendor divergence, taken from the reference client: which directory applies, and whether there is a
  // directory at all. Getting endpointType wrong is refused as a bare 404 with nothing to distinguish it from
  // "no such workflow", so the panel has to be specific about which values each audience may send.
  describe('Vendor differences', () => {
    // Provider Standalone launches against vendor-sandbox rows, and Segue accepts BOTH Epic and Ecw for it
    // (OAuthController's ProviderStandaloneEndpointTypes). Naming only Epic made the eCW half look unsupported.
    it('offers Provider Standalone both vendor directories, and Patient only MyChart', () => {
      const provider = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Standalone' }), ORIGIN, 'Provider Standalone');
      expect(valuesOf(provider)).toContain('endpointType=Epic');
      expect(hintsOf(provider)).toContain('endpointType=Ecw');

      const patient = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Patient' }), ORIGIN, 'Patient Standalone');
      expect(valuesOf(patient)).toContain('endpointType=MyChart');
      expect(hintsOf(patient)).not.toContain('endpointType=Ecw');
    });

    // A single-practice provider source has one fixed address and no directory, so the picker step does not
    // apply at all — but a patient always signs in to a named hospital, so for them it always does.
    it('says when the site picker can be skipped, and when it cannot', () => {
      const provider = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Standalone' }), ORIGIN, 'Provider Standalone');
      expect(valuesOf(provider)).toContain('ehrEndpointId is optional');

      const patient = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Patient' }), ORIGIN, 'Patient Standalone');
      expect(valuesOf(patient)).toContain('ehrEndpointId is required');
    });

    // One registered page serves every vendor; iss is the only thing distinguishing them.
    it('tells EHR Launch to route on iss and to cope with being framed', () => {
      const details = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'EhrLaunch' }), ORIGIN, 'EHR Launch');

      expect(valuesOf(details)).toContain('Read iss to decide which workflow to launch');
      expect(valuesOf(details)).toContain('Redirect the top-level window');
    });

    // Backend has no person, so borrowing an interactive sign-in is exactly the anti-pattern to warn against.
    it('tells Backend not to use the interactive identity values at all', () => {
      const details = buildIntegrationDetails(row(), ORIGIN, 'Backend Service');

      expect(valuesOf(details)).toContain('Do not send callerId, sessionId or userIdentity');
    });
  });

  describe('Headers and identity', () => {
    const AUDIENCES: [string, Partial<WorkflowSummary>][] = [
      ['Backend Service', {}],
      ['Provider Standalone', { action: 'Launch', applicationType: 'Standalone' }],
      ['Patient Standalone', { action: 'Launch', applicationType: 'Patient' }],
      ['EHR Launch', { action: 'Launch', applicationType: 'EhrLaunch' }],
    ];

    // EHR Launch's correlation line has always pointed at "correlationId from validate-run" while its own steps
    // never mentioned the call. The reference client (Demo_TestApp's backend mint) does call it — server-side,
    // before minting the context, and ONLY on the inbound leg carrying iss: validating the return leg too mints a
    // second id and strands an orphaned Execution History row per launch.
    it('documents validate-run on EHR Launch, gated to the inbound leg', () => {
      const details = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'EhrLaunch' }), ORIGIN, 'EHR Launch');
      const values = valuesOf(details);
      const text = values + '\n' + hintsOf(details);

      expect(values).toContain('/validate-run');
      expect(values.indexOf('/validate-run')).toBeLessThan(values.indexOf('/public-launch-context'));
      expect(text).toContain('iss and launch');
      expect(text).toContain('isValid');
    });

    // userIdentity is what the EHR access is permanently tied to, and the mint endpoint reads it — but the panel
    // only ever mentioned it in the framed-page note, never in the call that takes it.
    it('shows userIdentity on the EHR Launch mint call', () => {
      const details = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'EhrLaunch' }), ORIGIN, 'EHR Launch');

      expect(valuesOf(details)).toContain('public-launch-context?callerId={yourAppUrl}&userIdentity={accountId}');
    });

    // context_mismatch means the sign-in was rejected and nothing was saved — a different branch from
    // workflow_failed, which the panel previously presented as the only failure marker on this audience.
    it('documents context_mismatch on EHR Launch, not just workflow_failed', () => {
      const details = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'EhrLaunch' }), ORIGIN, 'EHR Launch');
      const text = valuesOf(details) + '\n' + hintsOf(details);

      expect(text).toContain('launchError=context_mismatch');
      expect(text).toContain('replaceState');
    });

    it('spells out the correlation requirement for every audience', () => {
      for (const [label, overrides] of AUDIENCES) {
        const details = buildIntegrationDetails(row(overrides), ORIGIN, label);

        expect(valuesOf(details)).toContain('X-Correlation-Id');
        expect(valuesOf(details).toLowerCase() + hintsOf(details).toLowerCase()).toContain('cookies');
      }
    });

    // POST /run is AllowAnonymous with [CsrfExempt]; a launch partner presents NO Segue credential — the run is
    // gated on IsPubliclyLaunchable plus the unguessable callerId. The panel used to tell every audience it needed
    // a workflow.run sign-in "sent as cookies", which for a launch partner means asking an admin for a credential
    // that neither exists nor is needed, and hunting a CSRF cookie the exempt endpoint never reads. Backend is the
    // one audience where the cookie/CSRF requirement is real.
    it('does not tell launch partners they need a Segue sign-in', () => {
      for (const [label, overrides] of AUDIENCES.slice(1)) {
        const details = buildIntegrationDetails(row(overrides), ORIGIN, label);
        const text = valuesOf(details) + '\n' + hintsOf(details);

        expect(details.checks.some(check => check.label === 'No Segue sign-in needed')).toBeTrue();
        expect(details.checks.some(check => check.label === 'Sign-in required to run')).toBeFalse();
        expect(text).toContain('callerId is the credential');
        // The CSRF header may be MENTIONED (to say it is not needed), but never presented as a requirement.
        expect(details.values.some(value => value.label.startsWith('Header — X-CSRF-Token'))).toBeFalse();
        expect(text).toContain('no X-CSRF-Token needed');
      }

      const backend = buildIntegrationDetails(row(), ORIGIN, 'Backend Service');
      expect(backend.checks.some(check => check.label === 'Sign-in required to run')).toBeTrue();
      expect(valuesOf(backend)).toContain('X-CSRF-Token');
    });

    // callerId / sessionId / userIdentity are three different things that all look like "an id for the caller".
    it('distinguishes the three identity values on the interactive audiences only', () => {
      for (const [label, overrides] of AUDIENCES.slice(1)) {
        const details = buildIntegrationDetails(row(overrides), ORIGIN, label);
        expect(valuesOf(details)).toContain('callerId  ·  sessionId  ·  userIdentity');
      }

      const backend = buildIntegrationDetails(row(), ORIGIN, 'Backend Service');
      expect(valuesOf(backend)).not.toContain('callerId  ·  sessionId  ·  userIdentity');
    });

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
  });

  describe('Copy as text', () => {
    it('carries the values and the blocking items into the pasted version', () => {
      const details = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Patient', isPubliclyLaunchable: false }),
        ORIGIN, 'Patient Standalone');

      const text = integrationDetailsAsText(details);

      expect(text).toContain('Patient Standalone');
      expect(text).toContain('endpointType=MyChart');
      // Blocking items must survive the copy — an emailed panel that hides them would mislead the partner.
      expect(text).toContain('[!!] Public launch allowed');
    });
  });
});
