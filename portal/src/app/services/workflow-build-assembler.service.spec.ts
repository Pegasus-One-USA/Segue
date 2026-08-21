import { TestBed } from '@angular/core/testing';
import { WorkflowBuildAssemblerService } from './workflow-build-assembler.service';
import { PipelineStore } from './pipeline.store';
import { WorkflowGraphMapperService } from './workflow-graph-mapper.service';
import { CreateSourceConnectionRequest } from './workflow-api.service';

/**
 * Regression coverage for the canonical source-type persistence bug: WorkflowBuildAssemblerService.buildSource()
 * used to have dedicated branches only for Sample/GenericFhir/Athenahealth, with every other connector — including
 * Cerner, Allscripts, Healow, MeditechGreenfield, and Hl7v2 — silently falling through to a hardcoded
 * `sourceSystemType: 'Epic'`. A Cerner (etc.) source node would build and persist as an Epic SourceConnection with
 * no error, no warning, and no way to tell from the API response that the wrong vendor was saved.
 *
 * buildSource() is private and touches only its own two parameters (never `this.store`/`this.mapper`), so
 * PipelineStore/WorkflowGraphMapperService are provided as empty stand-ins purely to satisfy the constructor's
 * `inject()` calls — this is a pure-function test of the request-building logic, not an integration test of the
 * full assemble() pipeline.
 */
describe('WorkflowBuildAssemblerService.buildSource', () => {
  let service: WorkflowBuildAssemblerService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        WorkflowBuildAssemblerService,
        { provide: PipelineStore, useValue: {} },
        { provide: WorkflowGraphMapperService, useValue: {} },
      ],
    });
    service = TestBed.inject(WorkflowBuildAssemblerService);
  });

  // Mirrors EhrVendorSourceFormComponent.buildFieldsToSave()'s always-present keys for a Backend-System-audience
  // connection (see that method's own doc comment) — the exact field shape every one of Epic/Cerner/Allscripts/
  // Healow/MeditechGreenfield/Athenahealth writes, varying only 'Connector' and the vendor-specific fields noted
  // per-vendor below.
  function ehrVendorFields(connector: string, overrides: Record<string, string> = {}): Record<string, string> {
    return {
      __name: `${connector} Source`,
      Connector: connector,
      'App key': 'backend-system',
      'App context': 'Backend system',
      'Client ID': 'client-123',
      Scopes: 'system/Patient.read system/Observation.read',
      'Discovered scopes': '',
      'Token endpoint': 'https://vendor.example.org/oauth2/token',
      'JWT kid': 'kid-1',
      'Key vault reference': 'workflow-secrets',
      'Secret Name': 'epic-private-key',
      'FHIR base URL': 'https://vendor.example.org/fhir',
      'Auth placement': 'post',
      'Retrieval method key': 'search-rest',
      ...overrides,
    };
  }

  function buildSource(fields: Record<string, string>): CreateSourceConnectionRequest {
    return (service as unknown as { buildSource: (f: Record<string, string>, d?: string[]) => CreateSourceConnectionRequest })
      .buildSource(fields);
  }

  describe('Epic-shaped vendors resolve to their own SourceSystemType, never a hardcoded Epic', () => {
    const cases: Array<{ connector: string; expected: string }> = [
      { connector: 'Epic', expected: 'Epic' },
      { connector: 'Cerner', expected: 'Cerner' },
      { connector: 'Allscripts', expected: 'Allscripts' },
      { connector: 'Healow', expected: 'Healow' },
      { connector: 'MeditechGreenfield', expected: 'MeditechGreenfield' },
    ];

    for (const { connector, expected } of cases) {
      it(`${connector} -> sourceSystemType '${expected}'`, () => {
        const request = buildSource(ehrVendorFields(connector));
        expect(request.sourceSystemType).toBe(expected);
      });
    }

    it('Cerner MUST NOT become Epic', () => {
      expect(buildSource(ehrVendorFields('Cerner')).sourceSystemType).not.toBe('Epic');
    });

    it('Allscripts MUST NOT become Epic', () => {
      expect(buildSource(ehrVendorFields('Allscripts')).sourceSystemType).not.toBe('Epic');
    });

    it('Healow MUST NOT become Epic', () => {
      expect(buildSource(ehrVendorFields('Healow')).sourceSystemType).not.toBe('Epic');
    });

    it('MeditechGreenfield MUST NOT become Epic', () => {
      expect(buildSource(ehrVendorFields('MeditechGreenfield')).sourceSystemType).not.toBe('Epic');
    });

    it('carries the real vendor-specific auth/config fields, not fabricated or Epic-copied ones', () => {
      const request = buildSource(ehrVendorFields('Cerner', {
        'Client ID': 'cerner-client-9',
        Scopes: 'system/Patient.read',
        'Token endpoint': 'https://cerner.example.org/oauth2/token',
        'JWT kid': 'cerner-kid',
        'FHIR base URL': 'https://cerner.example.org/fhir',
      }));
      expect(request.sourceSystemType).toBe('Cerner');
      expect(request.baseUrl).toBe('https://cerner.example.org/fhir');
      expect(request.authentication.clientId).toBe('cerner-client-9');
      expect(request.authentication.scopes).toEqual(['system/Patient.read']);
      expect(request.authentication.tokenEndpoint).toBe('https://cerner.example.org/oauth2/token');
      expect(request.authentication.keyId).toBe('cerner-kid');
      expect(request.authentication.authenticationType).toBe('SmartBackendServices');
    });

    it('falls back to name = sourceSystemType (not literal "Epic") when __name is blank', () => {
      const request = buildSource(ehrVendorFields('Healow', { __name: '' }));
      expect(request.name).toBe('Healow');
    });

    it('a legacy node with no Connector field at all still defaults to Epic (backward compatibility)', () => {
      const legacyFields = ehrVendorFields('Epic');
      delete (legacyFields as Record<string, string | undefined>)['Connector'];
      const request = buildSource(legacyFields);
      expect(request.sourceSystemType).toBe('Epic');
    });

    it('NewEHR/NewEHRTwo are not implemented and do not resolve to themselves (fall back to Epic like any unrecognized connector)', () => {
      expect(buildSource(ehrVendorFields('NewEHR')).sourceSystemType).toBe('Epic');
      expect(buildSource(ehrVendorFields('NewEHRTwo')).sourceSystemType).toBe('Epic');
    });
  });

  describe('vendors with their own dedicated branch are unaffected by this fix', () => {
    it('Sample -> sourceSystemType Sample', () => {
      const request = buildSource({ __name: 'Sample Source', Connector: 'Sample' });
      expect(request.sourceSystemType).toBe('Sample');
    });

    it('GenericFhir -> sourceSystemType GenericFhir', () => {
      const request = buildSource({
        __name: 'Generic FHIR Source',
        Connector: 'Generic FHIR R4',
        'FHIR base URL': 'https://fhir.example.org',
        'Retrieval method key': 'search-rest',
      });
      expect(request.sourceSystemType).toBe('GenericFhir');
    });

    it('Athenahealth -> sourceSystemType Athenahealth', () => {
      const request = buildSource(ehrVendorFields('Athenahealth'));
      expect(request.sourceSystemType).toBe('Athenahealth');
    });
  });

  describe('Hl7v2 — rejected explicitly, never silently mis-persisted as Epic', () => {
    function hl7v2Fields(): Record<string, string> {
      return {
        __name: 'HL7 v2 / MLLP',
        Connector: 'HL7 v2 / MLLP',
        'App context': 'Backend system',
        Protocol: 'HL7 v2 over MLLP',
        Auth: 'None (MLLP TCP stream)',
        Host: 'mllp.example.org',
        Port: '2575',
        'MLLP timeout (seconds)': '30',
      };
    }

    it('throws rather than returning a CreateSourceConnectionRequest at all', () => {
      expect(() => buildSource(hl7v2Fields())).toThrow();
    });

    it('the thrown error is a descriptive Error, not a silent Epic substitution', () => {
      try {
        buildSource(hl7v2Fields());
        fail('expected buildSource to throw for an Hl7v2 connector');
      } catch (err) {
        expect(err instanceof Error).toBe(true);
        expect((err as Error).message).toContain('HL7 v2');
      }
    });
  });
});
