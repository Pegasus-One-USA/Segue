import { TestBed } from '@angular/core/testing';
import { EMPTY, of } from 'rxjs';
import { EhrVendorSourceFormComponent } from './ehr-vendor-source-form.component';
import { WizardService } from '../../../services/wizard.service';
import { EpicDiscoveryService } from '../../../services/epic-discovery.service';
import { MappingCatalogService } from '../../../services/mapping-catalog.service';
import { EhrWriteCapabilitiesService } from '../../../services/ehr-write-capabilities.service';
import { UnsavedChangesPromptService } from '../../../core/services/unsaved-changes-prompt.service';
import { ISourceConnectionService } from '../../../source-connections/services/i-source-connection.service';
import { SourceConnectionModel } from '../../../source-connections/models/source-connection.model';
import { EhrVendor } from '../../../ehr-endpoints/models/ehr-endpoint.model';
import { PermissionService } from '../../../auth/services/permission.service';

/** The protected / private members these specs read back (the form-to-fields layer under test). */
interface FormInternals {
  form: {
    getRawValue(): Record<string, unknown>;
    controls: Record<string, { value: unknown; setValue(v: unknown): void }>;
  };
  audienceConfig(): unknown;
  existingConnections(): SourceConnectionModel[];
  buildFieldsToSave(v: Record<string, unknown>, aud: string, cfg: unknown, emitRecurrence: boolean): Record<string, string>;
}

/**
 * The v1 vendor form (the Source Connections page, and EHR write connections on the Destination Connections page). Sources
 * only read: in read purpose the form must never emit Access, activation or Department ID (WizardService then sends
 * null and the server keeps the saved values). In write purpose it emits them, and a new connection is Backend System
 * with Access Write.
 */
describe('EhrVendorSourceFormComponent — read / write purpose', () => {
  const ATHENA_RW = {
    id: 'a1', name: 'athena rw', sourceSystemType: 'Athenahealth', baseUrl: 'https://api.athena.example/fhir/r4',
    isEnabled: true, applicationType: 'Backend', access: 'ReadWrite', vendorWriteApisActivated: true, departmentId: '150',
    authentication: { authenticationType: 'OAuthClientCredentials', clientId: 'cid', practiceId: '195900', scopes: [] },
  } as unknown as SourceConnectionModel;

  let wiz: WizardService;
  let getAll: jasmine.Spy;
  let canEditWriteBack: boolean;

  beforeEach(() => {
    canEditWriteBack = true;
    getAll = jasmine.createSpy('getAll').and.returnValue(of([]));
    const forVendor = (vendor: string | null | undefined) => of({
      capabilities: [{ resourceType: 'Observation', requiresVendorActivation: vendor === 'Healow' || vendor === 'Athenahealth' }],
    });
    TestBed.configureTestingModule({
      imports: [EhrVendorSourceFormComponent],
      providers: [
        { provide: ISourceConnectionService, useValue: { getAll, create: () => of({}), update: () => of({}) } },
        {
          provide: EpicDiscoveryService,
          useValue: { discover: () => EMPTY, derivedScopes: () => EMPTY, testBackendAuthScopes: () => EMPTY },
        },
        { provide: MappingCatalogService, useValue: { resourceTypes: () => of(null) } },
        { provide: EhrWriteCapabilitiesService, useValue: { forVendor, writableResourceTypes: () => of(['Observation']) } },
        { provide: UnsavedChangesPromptService, useValue: { confirmLeave: () => of(true) } },
        { provide: PermissionService, useValue: { hasPermission: (code: string) => code !== 'ehrwriteback.edit' || canEditWriteBack } },
      ],
    });
    wiz = TestBed.inject(WizardService);
  });

  afterEach(() => wiz.close());

  function mount(vendor: EhrVendor) {
    const fixture = TestBed.createComponent(EhrVendorSourceFormComponent);
    fixture.componentRef.setInput('vendor', vendor);
    fixture.detectChanges();
    const internals = fixture.componentInstance as unknown as FormInternals;
    const fields = (): Record<string, string> => {
      const v = internals.form.getRawValue();
      return internals.buildFieldsToSave(v, v['audience'] as string, internals.audienceConfig(), false);
    };
    return { fixture, internals, fields, el: fixture.nativeElement as HTMLElement };
  }

  it('read purpose: re-opening a Read & Write athena connection emits no Access, activation or Department ID', () => {
    wiz.openEntity(ATHENA_RW);
    const { fixture, fields } = mount('Athenahealth');

    for (const out of [fields(), fixture.componentInstance.getFields() ?? {}]) {
      expect('Access' in out).toBeFalse();
      expect('Vendor write APIs activated' in out).toBeFalse();
      expect('Department ID' in out).toBeFalse();
    }
  });

  it('read purpose: the audience of a connection that also writes is fixed text, not a select', () => {
    wiz.openEntity(ATHENA_RW);
    const { el } = mount('Athenahealth');

    expect(el.querySelector('#eaf-audience')).toBeNull();
    const locked = el.querySelector('[data-testid="src-audience-locked"]') as HTMLInputElement;
    expect(locked.value).toBe('Backend System');
  });

  it('read purpose: a read-only connection keeps the audience select', () => {
    wiz.openEntity({ ...ATHENA_RW, access: 'Read' } as SourceConnectionModel);
    const { el } = mount('Athenahealth');

    expect(el.querySelector('#eaf-audience')).not.toBeNull();
    expect(el.querySelector('[data-testid="src-audience-locked"]')).toBeNull();
  });

  it('write purpose: emits Access, activation and a trimmed athena Department ID', () => {
    wiz.openEntity(ATHENA_RW, { purpose: 'write' });
    const { internals, fields } = mount('Athenahealth');
    internals.form.controls['departmentId'].setValue('  151 ');

    const out = fields();
    expect(out['Access']).toBe('ReadWrite');
    expect(out['Vendor write APIs activated']).toBe('true');
    expect(out['Department ID']).toBe('151');
  });

  it('write purpose: Epic emits Access but no activation or Department ID', () => {
    wiz.openEntity({ ...ATHENA_RW, sourceSystemType: 'Epic', access: 'Write' } as SourceConnectionModel, { purpose: 'write' });
    const { fields } = mount('Epic');

    const out = fields();
    expect(out['Access']).toBe('Write');
    expect('Vendor write APIs activated' in out).toBeFalse();
    expect('Department ID' in out).toBeFalse();
  });

  it('write purpose: a new eClinicalWorks connection is Backend System with Access Write', () => {
    wiz.openEntity(null, { purpose: 'write' });
    wiz.ehrType.set('Healow');
    const { internals, fields } = mount('Healow');

    expect(internals.form.controls['audience'].value).toBe('backend-system');
    expect(internals.form.controls['access'].value).toBe('Write');
    const out = fields();
    expect(out['Access']).toBe('Write');
    expect(out['Vendor write APIs activated']).toBe('false');
  });

  it('write purpose without ehrwriteback.edit: no activation switch and no activation field (the server needs it)', () => {
    canEditWriteBack = false;
    wiz.openEntity(null, { purpose: 'write' });
    wiz.ehrType.set('Athenahealth');
    const { el, fields } = mount('Athenahealth');

    expect(el.querySelector('#eaf-vendor-activation')).toBeNull();
    expect(el.querySelector('[data-testid="src-activation-needs-edit"]')).not.toBeNull();
    const out = fields();
    expect(out['Access']).toBe('Write');
    expect('Vendor write APIs activated' in out).toBeFalse();
  });

  it('the existing-connection picker hides write-only connections', () => {
    getAll.and.returnValue(of([
      { ...ATHENA_RW, id: 'w', name: 'write only', access: 'Write' },
      { ...ATHENA_RW, id: 'rw', name: 'read write', access: 'ReadWrite' },
      { ...ATHENA_RW, id: 'r', name: 'read', access: 'Read' },
    ] as SourceConnectionModel[]));
    wiz.open();
    const { internals } = mount('Athenahealth');

    expect(internals.existingConnections().map(c => c.id)).toEqual(['rw', 'r']);
  });
});
