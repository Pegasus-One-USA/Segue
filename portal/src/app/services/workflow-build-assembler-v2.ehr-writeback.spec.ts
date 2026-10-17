import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { WorkflowBuildAssemblerServiceV2 } from './workflow-build-assembler-v2.service';
import { EhrWriteCapabilitiesService } from './ehr-write-capabilities.service';
import { WorkflowNodeRequest } from './workflow-api.service';

/**
 * The EHR Write-Back node must assemble into its own destination type with every write-back setting carried in
 * ConnectionMetadataJson (the allow-list silently drops unknown keys) and no secret — falling through to the CSV
 * default, as every unrecognised destination does, would save a broken Csv destination instead.
 */
describe('WorkflowBuildAssemblerServiceV2 — EHR write-back destination', () => {
  let service: WorkflowBuildAssemblerServiceV2;

  const buildDestination = (fields: Record<string, string>, nodeType = 'EhrWriteBackDestinationNode') =>
    (
      service as unknown as {
        buildDestination: (f: Record<string, string>, n: WorkflowNodeRequest) => {
          destinationType: string;
          inlineSecret: string | null;
          target: string | null;
          connectionMetadataJson: string;
        };
      }
    ).buildDestination(fields, { nodeType } as WorkflowNodeRequest);

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [WorkflowBuildAssemblerServiceV2, provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(WorkflowBuildAssemblerServiceV2);
  });

  it('builds an EhrWriteBack destination with every setting and no secret', () => {
    const request = buildDestination({
      dest_name: 'Epic write-back',
      dest_sourceConnectionId: '11111111-1111-1111-1111-111111111111',
      dest_ehrVendor: 'Epic',
      dest_dryRun: 'true',
      dest_createPatientIfMissing: 'false',
      dest_maxWritesPerRun: '250',
      dest_noteDocStatus: 'preliminary',
      dest_resources: 'AllergyIntolerance,Condition',
    });

    expect(request.destinationType).toBe('EhrWriteBack');
    expect(request.inlineSecret).toBe('');
    expect(request.target).toBeNull();
    const metadata = JSON.parse(request.connectionMetadataJson) as Record<string, string>;
    expect(metadata['dest_sourceConnectionId']).toBe('11111111-1111-1111-1111-111111111111');
    expect(metadata['dest_maxWritesPerRun']).toBe('250');
    expect(metadata['dest_dryRun']).toBe('true');
    // A node field written by the wizard, not connection metadata.
    expect(metadata['dest_resources']).toBeUndefined();
  });

  it('carries the eClinicalWorks and athena settings', () => {
    const request = buildDestination({
      dest_name: 'athena write-back',
      dest_sourceConnectionId: '11111111-1111-1111-1111-111111111111',
      dest_createHolderEncounter: 'true',
      dest_targetProviderId: '71',
      dest_targetDepartmentId: '150',
    });

    const metadata = JSON.parse(request.connectionMetadataJson) as Record<string, string>;
    expect(metadata['dest_createHolderEncounter']).toBe('true');
    expect(metadata['dest_targetProviderId']).toBe('71');
    expect(metadata['dest_targetDepartmentId']).toBe('150');
  });

  it('recognises the node by its transform id as well as its node type', () => {
    const request = buildDestination({ __transformId: 'dest-ehr-writeback', dest_name: 'x' }, 'SomethingElse');

    expect(request.destinationType).toBe('EhrWriteBack');
  });
});

describe('EhrWriteCapabilitiesService', () => {
  let service: EhrWriteCapabilitiesService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(EhrWriteCapabilitiesService);
    http = TestBed.inject(HttpTestingController);
  });

  it('treats a failed call as no write capability, never as "no filter"', () => {
    let result: string[] | undefined;
    service.writableResourceTypes('Epic').subscribe((types) => (result = types));

    http.expectOne((req) => req.url.includes('/ehr-write-capabilities?vendor=Epic')).flush('boom', { status: 500, statusText: 'Server Error' });

    expect(result).toEqual([]);
  });

  it('lists a type once even when the vendor files it through several APIs', () => {
    let result: string[] | undefined;
    service.writableResourceTypes('Healow').subscribe((types) => (result = types));

    http.expectOne((req) => req.url.includes('vendor=Healow')).flush({
      vendor: 'Healow', supportsPatientMatch: false, cloneModeEnabled: false,
      capabilities: ['Condition', 'Condition', 'Observation', 'Condition'].map((resourceType) => ({ resourceType })),
    });

    expect(result).toEqual(['Condition', 'Observation']);
  });

  it('asks nothing for a missing vendor', () => {
    let result: string[] | undefined;
    service.writableResourceTypes(null).subscribe((types) => (result = types));

    http.expectNone(() => true);
    expect(result).toEqual([]);
  });
});
