import { TestBed } from '@angular/core/testing';
import { PhaseConfigService } from './phase-config.service';
import { PhaseConfigServiceV2 } from './phase-config-v2.service';

/**
 * Generic FHIR R4 (any conformant, unauthenticated FHIR R4 server) is wired end to end — source form,
 * WorkflowBuildAssemblerV2's GenericFhir branch, the runtime GenericFhirSourceNode/GenericFhirSourceClient — so
 * the active phase must offer it. Both configs gate it: V2 drives the Workflow Builder's source picker, V1 the
 * Source Connections list, Workflows list and RBAC screens.
 */
describe('Phase config — Generic FHIR source', () => {
  const PHASE_1_SOURCES = ['epic', 'athena', 'healow', 'generic-fhir'];

  it('V2 (Workflow Builder source picker) enables generic-fhir alongside the existing sources', () => {
    const phase = TestBed.inject(PhaseConfigServiceV2);

    expect(PHASE_1_SOURCES.filter(id => !phase.isSourceEnabled(id))).toEqual([]);
  });

  it('V1 (Source Connections, Workflows, RBAC screens) enables generic-fhir alongside the existing sources', () => {
    const phase = TestBed.inject(PhaseConfigService);

    expect(PHASE_1_SOURCES.filter(id => !phase.isSourceEnabled(id))).toEqual([]);
  });
});
