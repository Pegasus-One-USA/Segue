import { TestBed } from '@angular/core/testing';
import { EMPTY, of } from 'rxjs';
import { EhrVendorSourceFormComponent } from './ehr-vendor-source-form.component';
import { WizardServiceV2 } from '../../../services/wizard-v2.service';
import { PipelineStoreV2 } from '../../../services/pipeline-v2.store';
import { EpicDiscoveryService } from '../../../services/epic-discovery.service';
import { MappingCatalogService } from '../../../services/mapping-catalog.service';
import { EhrWriteCapabilitiesService } from '../../../services/ehr-write-capabilities.service';
import { UnsavedChangesPromptService } from '../../../core/services/unsaved-changes-prompt.service';
import { ISourceConnectionService } from '../../../source-connections/services/i-source-connection.service';
import { EhrVendor } from '../../../ehr-endpoints/models/ehr-endpoint.model';
import { PermissionService } from '../../../auth/services/permission.service';
import { CanvasNode } from '../../../models/node-v2.model';
import { SUPPORTED_RESOURCE_TYPES } from '../../../data/scope-constants-v2.data';
import { SOURCE_TYPES_DECLARED_KEY, declaredSourceResourceTypes } from '../../../services/upstream-source-v2.util';

/** The protected / private members these specs read back. */
interface FormInternals {
  form: {
    getRawValue(): Record<string, unknown>;
    controls: Record<string, { value: unknown; setValue(v: unknown): void }>;
  };
  audienceConfig(): unknown;
  declaredResourceTypes(): string[];
  readableResourceTypes(): string[];
  keepsLegacyTypes(): boolean;
  resourceTypesMissing(): boolean;
  onDeclaredResourceTypesChange(types: string[]): void;
  existingConnections: { set(v: unknown[]): void };
  onExistingConnectionSelected(id: string): void;
  buildFieldsToSave(v: Record<string, unknown>, aud: string, cfg: unknown, emitRecurrence: boolean): Record<string, string>;
}

/**
 * The v2 EHR source form's "Resource types to read" (canvas mode): what a source node declares it reads. A node saved
 * before sources declared their types carries no marker and stays legacy until the admin changes the list; types
 * move with an audience switch; the picker offers only what the vendor can read and be scoped for.
 */
describe('EhrVendorSourceFormComponent (v2) — resource types to read', () => {
  let wiz: WizardServiceV2;
  let store: PipelineStoreV2;
  let vendorTypes: string[] | null;

  beforeEach(() => {
    vendorTypes = null;
    TestBed.configureTestingModule({
      imports: [EhrVendorSourceFormComponent],
      providers: [
        {
          provide: ISourceConnectionService,
          useValue: { getAll: () => of([]), getById: () => of(null), create: () => of({}), update: () => of({}) },
        },
        {
          provide: EpicDiscoveryService,
          useValue: { discover: () => EMPTY, derivedScopes: () => EMPTY, testBackendAuthScopes: () => EMPTY },
        },
        { provide: MappingCatalogService, useValue: { resourceTypes: () => of(vendorTypes) } },
        { provide: EhrWriteCapabilitiesService, useValue: { forVendor: () => of({ capabilities: [] }), writableResourceTypes: () => of([]) } },
        { provide: UnsavedChangesPromptService, useValue: { confirmLeave: () => of(true) } },
        { provide: PermissionService, useValue: { hasPermission: () => true } },
      ],
    });
    wiz = TestBed.inject(WizardServiceV2);
    store = TestBed.inject(PipelineStoreV2);
  });

  function mount(vendor: EhrVendor, savedFields?: Record<string, string>) {
    if (savedFields) {
      store.nodes.set([{ id: 'n1', x: 0, y: 0, connected: true, fields: savedFields } as CanvasNode]);
      store.editingNodeId.set('n1');
      wiz.open('n1');
    } else {
      wiz.open();
    }
    const fixture = TestBed.createComponent(EhrVendorSourceFormComponent);
    fixture.componentRef.setInput('vendor', vendor);
    fixture.detectChanges();
    const internals = fixture.componentInstance as unknown as FormInternals;
    const fields = (): Record<string, string> => {
      const v = internals.form.getRawValue();
      return internals.buildFieldsToSave(v, v['audience'] as string, internals.audienceConfig(), false);
    };
    return { fixture, internals, fields };
  }

  it('an old Epic Standalone node (silent full search-rest list, no marker) is legacy until its list is changed', () => {
    const silent = SUPPORTED_RESOURCE_TYPES.join(', ');
    const { internals, fields } = mount('Epic', {
      __name: 'Epic standalone',
      'Epic audience': 'provider-standalone',
      'App key': 'provider-standalone',
      Resources: '',
      'Retrieval method key': 'search-rest',
      'Retrieval resource type': silent,
    });
    expect(internals.keepsLegacyTypes()).toBeTrue();
    expect(internals.resourceTypesMissing()).toBeFalse();
    expect(fields()[SOURCE_TYPES_DECLARED_KEY]).toBeUndefined();
    expect(fields()['Retrieval resource type']).toBe(silent);

    internals.onDeclaredResourceTypesChange(['Patient']);
    const out = fields();
    expect(out[SOURCE_TYPES_DECLARED_KEY]).toBe('true');
    // The build prefers 'Retrieval resource type' over 'Resources': it must now be the declared list, not the old one.
    expect(out['Retrieval resource type']).toBe('Patient');
  });

  it('an old athena Backend node with empty lists may be re-saved as it is, with nothing seeded', () => {
    vendorTypes = ['Patient', 'Condition'];
    const { internals, fields } = mount('Athenahealth', {
      __name: 'athena',
      'Epic audience': 'backend-system',
      'App key': 'backend-system',
      'Retrieval method key': 'search-rest',
    });
    expect(internals.declaredResourceTypes()).toEqual([]);
    expect(internals.resourceTypesMissing()).toBeFalse();
    const out = fields();
    expect(out['Retrieval resource type']).toBe('');
    expect(declaredSourceResourceTypes({ ...out, Resources: '' })).toBeNull();
  });

  it('a new node must declare at least one type', () => {
    const { internals } = mount('Epic');
    expect(internals.declaredResourceTypes()).toEqual([]);
    expect(internals.resourceTypesMissing()).toBeTrue();
  });

  it('switching Provider Standalone to Backend System carries the declared types onto the retrieval method', () => {
    const { internals, fixture } = mount('Epic');
    internals.form.controls['audience'].setValue('provider-standalone');
    fixture.detectChanges();
    internals.onDeclaredResourceTypesChange(['Patient', 'Condition']);
    expect(internals.declaredResourceTypes()).toEqual(['Patient', 'Condition']);

    internals.form.controls['audience'].setValue('backend-system');
    fixture.detectChanges();
    expect(internals.declaredResourceTypes()).toEqual(['Patient', 'Condition']);
  });

  it('switching the Backend System retrieval method carries the declared types across', () => {
    const { internals, fixture } = mount('Epic');
    internals.form.controls['audience'].setValue('backend-system');
    internals.form.controls['retrievalMethod'].setValue('search-rest');
    fixture.detectChanges();
    internals.onDeclaredResourceTypesChange(['Observation']);

    internals.form.controls['retrievalMethod'].setValue('bulk-export');
    fixture.detectChanges();
    expect(internals.declaredResourceTypes()).toEqual(['Observation']);
  });

  it('eClinicalWorks Backend offers only types it publishes a system/ read scope for', () => {
    vendorTypes = ['Patient', 'Coverage', 'Task', 'Communication'];
    const { internals, fixture } = mount('Healow');
    internals.form.controls['audience'].setValue('backend-system');
    fixture.detectChanges();
    expect(internals.readableResourceTypes()).toEqual(jasmine.arrayWithExactContents(['Patient', 'Coverage']));
  });

  it('a new node started from an existing connection declares nothing until the admin picks', () => {
    vendorTypes = ['Patient', 'Condition', 'Organization'];
    const { internals, fixture, fields } = mount('Athenahealth');
    internals.existingConnections.set([
      {
        id: 'c1',
        name: 'athena shared',
        sourceSystemType: 'Athenahealth',
        baseUrl: 'https://api.preview.platform.athenahealth.com/fhir/r4',
        authentication: { authenticationType: 'ClientSecret', scopes: ['system/Patient.read'] },
        isEnabled: true,
        applicationType: 'Backend',
        // What the scope sync last wrote: every workflow sharing the connection, plus auto-fetch targets.
        retrieval: { retrievalMethod: 'search-rest', resourceTypes: ['Patient', 'Condition', 'Organization'] },
      },
    ]);
    internals.onExistingConnectionSelected('c1');
    fixture.detectChanges();

    expect(internals.declaredResourceTypes()).toEqual([]);
    expect(internals.keepsLegacyTypes()).toBeFalse();
    expect(internals.resourceTypesMissing()).toBeTrue();

    internals.onDeclaredResourceTypesChange(['Patient']);
    const out = fields();
    expect(out[SOURCE_TYPES_DECLARED_KEY]).toBe('true');
    expect(out['Retrieval resource type']).toBe('Patient');
  });

  it('athenahealth offers nothing while its verified list is unknown, rather than everything', () => {
    vendorTypes = null;
    const { internals } = mount('Athenahealth');
    expect(internals.readableResourceTypes()).toEqual([]);
  });
});
