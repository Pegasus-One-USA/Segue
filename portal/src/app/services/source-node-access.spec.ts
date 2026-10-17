import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { WorkflowBuildAssemblerServiceV2 } from './workflow-build-assembler-v2.service';
import { CreateSourceConnectionRequest } from './workflow-api.service';
import { WizardServiceV2 } from './wizard-v2.service';
import { WizardFormValues } from './wizard.service';
import { ISourceConnectionService } from '../source-connections/services/i-source-connection.service';
import { SourceConnectionRequest } from '../source-connections/models/source-connection.model';

/**
 * A workflow source node only reads. Saving one must send Access and "Vendor write APIs activated" as null (Read on
 * create, keep saved on update) even when an old node still carries those fields — otherwise re-saving an old source
 * node would downgrade a Write / Read & Write connection or clear its activation.
 */
describe('Source node save — access', () => {
  const oldWriteFields = {
    Access: 'Write',
    'Vendor write APIs activated': 'true',
  };

  describe('WorkflowBuildAssemblerServiceV2.buildSource', () => {
    let service: WorkflowBuildAssemblerServiceV2;
    const buildSource = (fields: Record<string, string>): CreateSourceConnectionRequest =>
      (service as unknown as { buildSource: (f: Record<string, string>, d: string[]) => CreateSourceConnectionRequest })
        .buildSource(fields, []);

    beforeEach(() => {
      TestBed.configureTestingModule({
        providers: [WorkflowBuildAssemblerServiceV2, provideHttpClient(), provideHttpClientTesting()],
      });
      service = TestBed.inject(WorkflowBuildAssemblerServiceV2);
    });

    it('sends null for an Epic node that still carries Access = Write', () => {
      const request = buildSource({
        Connector: 'Epic', __name: 'Epic', 'Epic audience': 'backend-system', 'App key': 'backend-system',
        'Auth method': 'jwt', 'Client ID': 'abc', 'FHIR base URL': 'https://epic.example/fhir', ...oldWriteFields,
      });

      expect(request.access).toBeNull();
      expect(request.vendorWriteApisActivated).toBeNull();
    });

    it('sends null for a Generic FHIR node that still carries Access = Write', () => {
      const request = buildSource({
        Connector: 'Generic FHIR R4', __name: 'HAPI', 'FHIR base URL': 'https://hapi.example/fhir', ...oldWriteFields,
      });

      expect(request.access).toBeNull();
      expect(request.vendorWriteApisActivated).toBeNull();
    });
  });

  describe('WizardServiceV2.save (entity mode)', () => {
    it('sends null even when the fields carry Access = Write', () => {
      const create = jasmine.createSpy('create').and.returnValue(of({}));
      TestBed.configureTestingModule({
        providers: [{ provide: ISourceConnectionService, useValue: { create, update: create } }],
      });
      const wiz = TestBed.inject(WizardServiceV2);
      const formValues: WizardFormValues = {
        stepName: 'Conn', baseUrl: 'https://ehr.example/fhir', token: '', authorize: '', algorithm: 'RS384',
        jwksMethod: 'hosted', jwksUrl: '', kid: '', kvRef: '', redirectUri: '', launchUrl: '',
      };

      wiz.openEntity(null);
      wiz.save(formValues, { 'Epic audience': 'backend-system', 'Auth method': 'jwt', 'Client ID': 'abc', ...oldWriteFields });
      wiz.close();

      const request = create.calls.mostRecent().args[0] as SourceConnectionRequest;
      expect(request.access).toBeNull();
      expect(request.vendorWriteApisActivated).toBeNull();
    });
  });
});
