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
    it('never offers the run endpoint — these cannot be started from a server', () => {
      for (const applicationType of ['Standalone', 'Patient', 'EhrLaunch']) {
        const details = buildIntegrationDetails(
          row({ action: 'Launch', applicationType }), ORIGIN, applicationType);

        expect(valuesOf(details)).not.toContain('/run');
      }
    });

    it('sends Provider Standalone to the Epic directory and its own mint endpoint', () => {
      const details = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'Standalone' }), ORIGIN, 'Provider Standalone');

      expect(details.kind).toBe('standalone');
      expect(valuesOf(details)).toContain('endpointType=Epic');
      expect(valuesOf(details)).toContain('/public-standalone-url');
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

    it('gives EHR Launch the two URLs the hospital registers, not a link to open', () => {
      const details = buildIntegrationDetails(
        row({ action: 'Launch', applicationType: 'EhrLaunch' }), ORIGIN, 'EHR Launch (Provider)');

      expect(details.kind).toBe('ehr-launch');
      expect(valuesOf(details)).toContain('/oauth/launch');
      expect(valuesOf(details)).toContain(`${ORIGIN}/api/v1/oauth/callback`);
      expect(valuesOf(details)).not.toContain('public-standalone-url');
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
