import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { NEVER, Observable, Subject, of, throwError } from 'rxjs';
import { EhrWriteBackDestinationFormComponent } from './ehr-write-back-destination-form.component';
import { ISourceConnectionService } from '../../../../source-connections/services/i-source-connection.service';
import { SourceConnectionModel } from '../../../../source-connections/models/source-connection.model';
import { EhrWriteCapabilities, EhrWriteCapabilitiesService, EhrWriteCapability } from '../../../../services/ehr-write-capabilities.service';
import { PermissionService } from '../../../../auth/services/permission.service';
import { EhrRunMode, EhrWriteVendor } from './ehr-write-back/ehr-write-back.model';

function capability(resourceType: string, extra: Partial<EhrWriteCapability> = {}): EhrWriteCapability {
  return {
    resourceType, operations: ['Create'], vendorApiId: 'x', variant: null, requiresEncounter: false, optInOnly: false,
    allowedApplicationTypes: ['Backend'], liveWriteSupported: true, ...extra,
  };
}

const BY_VENDOR: Record<string, EhrWriteCapabilities> = {
  GenericFhir: {
    vendor: 'GenericFhir', supportsPatientMatch: true, cloneModeEnabled: false,
    capabilities: [capability('AllergyIntolerance'), capability('Goal')],
    testableVendors: ['Epic', 'Healow', 'Athenahealth'],
  },
  Epic: {
    vendor: 'Epic', supportsPatientMatch: true, cloneModeEnabled: false,
    capabilities: [
      capability('AllergyIntolerance'),
      capability('Observation', { variant: 'vital-signs' }),
      capability('Observation', { variant: 'lines-drains-airways', requiresVariantOptIn: true }),
      capability('QuestionnaireResponse', { variant: 'patient-entered-questionnaire', requiresVariantOptIn: true, requiresTargetReferences: true }),
      capability('Procedure', { variant: 'external-radiotherapy-summary', requiresVariantOptIn: true }),
      capability('ServiceRequest', { variant: 'external-radiotherapy-summary', requiresVariantOptIn: true }),
    ],
  },
  Healow: {
    vendor: 'Healow', supportsPatientMatch: false, cloneModeEnabled: false,
    capabilities: [capability('AllergyIntolerance', { requiresVendorActivation: true }), capability('Condition', { createsHolderEncounter: true })],
  },
  Athenahealth: {
    vendor: 'Athenahealth', supportsPatientMatch: true, cloneModeEnabled: false,
    capabilities: [capability('AllergyIntolerance', { requiresVendorActivation: true })],
  },
};

const conn = (id: string, sourceSystemType: string, extra: Partial<SourceConnectionModel> = {}) =>
  ({ id, name: id, sourceSystemType, isEnabled: true, access: 'Write', ...extra }) as SourceConnectionModel;
const HAPI = conn('hapi', 'GenericFhir', { name: 'Local HAPI' });
const EPIC = conn('epic', 'Epic');
const ECW = conn('ecw', 'Healow');
const ATHENA = conn('ath', 'Athenahealth', { departmentId: '150' });
const ALL = [HAPI, EPIC, ECW, ATHENA];

const ALL_RIGHTS = ['sourceconnections.view', 'ehrwriteback.create', 'epic.create', 'healow.create', 'athenahealth.create', 'genericfhir.create'];

interface Options {
  connections?: SourceConnectionModel[];
  vendor?: EhrWriteVendor | null;
  granted?: string[];
  getAll?: () => Observable<SourceConnectionModel[]>;
  /** Vendors whose own capabilities never arrive (a test target still loading). */
  pendingVendors?: string[];
}

/**
 * The EHR Write-Back form: one tile per EHR presets the vendor, Run mode (Live / Dry run / Test on a FHIR server)
 * decides which write connections fit, and the saved keys stay dest_dryRun + dest_testAsVendor.
 */
describe('EhrWriteBackDestinationFormComponent', () => {
  function create(options: Options = {}) {
    const granted = new Set(options.granted ?? ALL_RIGHTS);
    const getAll = jasmine.createSpy('getAll').and.callFake(options.getAll ?? (() => of(options.connections ?? ALL)));
    TestBed.configureTestingModule({
      imports: [EhrWriteBackDestinationFormComponent],
      providers: [
        { provide: ISourceConnectionService, useValue: { getAll } },
        {
          provide: EhrWriteCapabilitiesService,
          useValue: {
            forVendor: (vendor: string) =>
              (options.pendingVendors ?? []).includes(vendor) ? NEVER : of(BY_VENDOR[vendor]),
          },
        },
        {
          provide: PermissionService,
          useValue: {
            hasPermission: (code: string) => granted.has(code),
            hasAll: (codes: string[]) => codes.every(c => granted.has(c)),
            hasAny: (codes: string[]) => codes.some(c => granted.has(c)),
          },
        },
      ],
    });

    const fixture = TestBed.createComponent(EhrWriteBackDestinationFormComponent);
    fixture.componentRef.setInput('ehrVendor', options.vendor === undefined ? null : options.vendor);
    fixture.detectChanges();
    const form = fixture.componentInstance;
    const el = fixture.nativeElement as HTMLElement;
    const pick = (id: string) => {
      form.form.controls.sourceConnectionId.setValue(id);
      fixture.detectChanges();
    };
    const mode = (m: EhrRunMode) => {
      form.form.controls.runMode.setValue(m);
      fixture.detectChanges();
    };
    const optionIds = () => Array.from(el.querySelectorAll<HTMLOptionElement>('#dw-ewb-target option')).map(o => o.value).filter(Boolean);
    const modes = () => Array.from(el.querySelectorAll<HTMLInputElement>('input[type="radio"]')).map(r => r.dataset['mode']);
    const notice = () => el.querySelector('[data-testid="ewb-connection-notice"]')?.textContent?.trim() ?? null;
    return { fixture, form, el, getAll, pick, mode, optionIds, modes, notice };
  }

  describe('a vendor tile', () => {
    it('offers only that vendor\'s write connections, and in a test run only FHIR servers that can stand in for it', () => {
      const { optionIds, mode } = create({ vendor: 'Epic' });
      expect(optionIds()).toEqual(['epic']);

      mode('test');
      expect(optionIds()).toEqual(['hapi']);
    });

    it('starts as a dry run and offers Live, Dry run and Test', () => {
      const { form, modes } = create({ vendor: 'Athenahealth' });
      expect(form.runMode()).toBe('dryRun');
      expect(modes()).toEqual(['live', 'dryRun', 'test']);
    });

    it('saves each run mode as dest_dryRun + dest_testAsVendor, with the tile as dest_ehrVendor', () => {
      const { form, pick, mode } = create({ vendor: 'Epic' });
      pick('epic');

      mode('live');
      expect(form.getFullConfig()).toEqual(jasmine.objectContaining({ dest_dryRun: 'false', dest_testAsVendor: '', dest_ehrVendor: 'Epic' }));
      mode('dryRun');
      expect(form.getFullConfig()).toEqual(jasmine.objectContaining({ dest_dryRun: 'true', dest_testAsVendor: '', dest_ehrVendor: 'Epic' }));
      mode('test');
      pick('hapi');
      expect(form.getFullConfig()).toEqual(jasmine.objectContaining({
        dest_dryRun: 'false', dest_testAsVendor: 'Epic', dest_ehrVendor: 'Epic', dest_sourceConnectionId: 'hapi',
      }));
    });

    it('takes the tested vendor\'s types and opt-in APIs in a test run', () => {
      const { form, pick, mode } = create({ vendor: 'Epic' });
      mode('test');
      pick('hapi');

      expect(form.targetVendor()).toBe('Epic');
      expect(form.writableResourceTypes()).toEqual(['AllergyIntolerance', 'Observation', 'QuestionnaireResponse', 'Procedure', 'ServiceRequest']);
      expect(form.effective()!.optInApis.map(api => api.variant))
        .toEqual(['lines-drains-airways', 'patient-entered-questionnaire', 'external-radiotherapy-summary']);
      expect(form.effective()!.optInApis.find(api => api.variant === 'patient-entered-questionnaire')!.tabularOnly).toBeTrue();
    });

    it('records the tile as dest_ehrVendor even before the tested vendor\'s capabilities load, and is not valid yet', () => {
      const { form, pick, mode } = create({ vendor: 'Epic', pendingVendors: ['Epic'] });
      mode('test');
      pick('hapi');

      expect(form.getFullConfig()['dest_ehrVendor']).toBe('Epic');
      expect(form.isValid()).toBeFalse();
      expect(form.getMetadata()).toBeNull();
    });

    it('a changed run mode reports a connection it no longer offers, without changing the form behind the user', () => {
      const { form, pick, mode, notice } = create({ vendor: 'Epic' });
      pick('epic');
      expect(form.isValid()).toBeTrue();

      mode('test');

      expect(form.form.controls.sourceConnectionId.value).toBe('epic');
      expect(notice()).toContain('does not fit this run mode');
      expect(form.isValid()).toBeFalse();

      pick('hapi');
      expect(notice()).toBeNull();
    });

    it('saves only the enabled APIs the vendor written as offers', () => {
      const { form, pick, mode } = create({ vendor: 'Epic' });
      mode('test');
      pick('hapi');

      form.toggleVariant('lines-drains-airways');
      form.toggleVariant('external-radiotherapy-summary');
      form.toggleVariant('lines-drains-airways');
      expect(form.getFullConfig()['dest_enabledVariants']).toBe('external-radiotherapy-summary');
    });
  });

  it('the FHIR server tile offers Live and Dry run only, over FHIR servers', () => {
    const { modes, optionIds, form, pick, mode } = create({ vendor: 'GenericFhir' });
    expect(modes()).toEqual(['live', 'dryRun']);
    expect(optionIds()).toEqual(['hapi']);

    pick('hapi');
    mode('live');
    expect(form.getFullConfig()).toEqual(jasmine.objectContaining({ dest_dryRun: 'false', dest_testAsVendor: '', dest_ehrVendor: 'GenericFhir' }));
  });

  describe('reopening a saved node', () => {
    it('restores a test run', () => {
      const { form, fixture, optionIds } = create({ vendor: 'Epic' });
      form.patchFrom({ dest_sourceConnectionId: 'hapi', dest_dryRun: 'false', dest_testAsVendor: 'Epic', dest_enabledVariants: 'lines-drains-airways' });
      fixture.detectChanges();

      expect(form.runMode()).toBe('test');
      expect(optionIds()).toEqual(['hapi']);
      expect(form.form.controls.sourceConnectionId.value).toBe('hapi');
      expect(form.targetVendor()).toBe('Epic');
      expect(form.enabledVariants()).toEqual(['lines-drains-airways']);
      expect(form.getFullConfig()['dest_enabledVariants']).toBe('lines-drains-airways');
    });

    it('reopens a test run that was also a dry run as a plain dry run, asking for another connection', () => {
      const { form, fixture, notice, pick } = create({ vendor: 'Epic' });
      form.patchFrom({ dest_sourceConnectionId: 'hapi', dest_dryRun: 'true', dest_testAsVendor: 'Epic' });
      fixture.detectChanges();

      expect(form.runMode()).toBe('dryRun');
      expect(notice()).toContain('used to dry-run against a FHIR test server');
      expect(form.isValid()).toBeFalse();
      // Kept until the user chooses, so a save that never showed this form cannot rewrite it.
      expect(form.getFullConfig()['dest_sourceConnectionId']).toBe('hapi');

      pick('epic');
      expect(notice()).toBeNull();
      expect(form.isValid()).toBeTrue();
    });

    it('a node saved without dest_dryRun is a dry run', () => {
      const { form, fixture } = create({ vendor: 'Epic' });
      form.patchFrom({ dest_sourceConnectionId: 'epic' });
      fixture.detectChanges();

      expect(form.runMode()).toBe('dryRun');
      expect(form.getFullConfig()['dest_dryRun']).toBe('true');
    });

    it('reports a saved connection that is no longer available', () => {
      const { form, fixture, notice } = create({ vendor: 'Epic' });
      form.patchFrom({ dest_sourceConnectionId: 'gone', dest_dryRun: 'false' });
      fixture.detectChanges();

      expect(notice()).toContain('no longer available');
      expect(form.isValid()).toBeFalse();
      expect(form.form.controls.sourceConnectionId.value).toBe('gone');
    });

    it('reports a saved connection only once the connections have loaded', () => {
      const pending = new Subject<SourceConnectionModel[]>();
      const { form, fixture, notice } = create({ vendor: 'Epic', getAll: () => pending });
      form.patchFrom({ dest_sourceConnectionId: 'gone' });
      fixture.detectChanges();
      expect(notice()).toBeNull();

      pending.next(ALL);
      pending.complete();
      fixture.detectChanges();
      expect(notice()).toContain('no longer available');
    });

    // The form stays mounted, hidden, while a chain node in front of the write-back is edited; what it reports as its
    // config must then still be what the node was saved with.
    const unlisted: [string, Options][] = [
      ['without the right to list connections', { granted: [] }],
      ['when listing them is refused', { getAll: () => throwError(() => new HttpErrorResponse({ status: 403 })) }],
      ['when listing them fails', { getAll: () => throwError(() => new HttpErrorResponse({ status: 500 })) }],
    ];
    for (const [why, options] of unlisted) {
      it(`keeps a legacy dry-run test node's connection ${why}`, () => {
        const { form, fixture, notice } = create({ vendor: 'Epic', ...options });
        form.patchFrom({ dest_sourceConnectionId: 'hapi', dest_dryRun: 'true', dest_testAsVendor: 'Epic' });
        fixture.detectChanges();

        expect(form.form.controls.sourceConnectionId.value).toBe('hapi');
        expect(form.getFullConfig()['dest_sourceConnectionId']).toBe('hapi');
        expect(form.runMode()).toBe('dryRun');
        expect(notice()).toBeNull();
      });
    }
  });

  describe('without a tile', () => {
    it('infers the vendor from a saved test run', () => {
      const { form, fixture, modes } = create({ vendor: null });
      form.patchFrom({ dest_sourceConnectionId: 'hapi', dest_dryRun: 'false', dest_testAsVendor: 'Healow' });
      fixture.detectChanges();

      expect(form.vendor()).toBe('Healow');
      expect(modes()).toEqual(['live', 'dryRun', 'test']);
      expect(form.form.controls.sourceConnectionId.value).toBe('hapi');
      expect(form.getFullConfig()).toEqual(jasmine.objectContaining({ dest_testAsVendor: 'Healow', dest_ehrVendor: 'Healow' }));
    });

    it('with nothing saved, lists every connection and offers no Test', () => {
      const { modes, optionIds, el } = create({ vendor: null });
      expect(optionIds()).toEqual(['hapi', 'epic', 'ecw', 'ath']);
      expect(modes()).toEqual(['live', 'dryRun']);
      expect(el.textContent).toContain('Pick a connection to see every run mode.');
    });

    it('takes the vendor of the connection chosen', () => {
      const { form, pick } = create({ vendor: null });
      pick('ath');
      expect(form.vendor()).toBe('Athenahealth');
      expect(form.getFullConfig()['dest_ehrVendor']).toBe('Athenahealth');
    });

    it('keeps the vendor chosen before switching to Test, so a test server can be picked', () => {
      const { form, pick, mode, optionIds, notice } = create({ vendor: null });
      pick('epic');
      mode('test');

      expect(form.vendor()).toBe('Epic');
      expect(optionIds()).toEqual(['hapi']);

      pick('hapi');
      expect(notice()).toBeNull();
      expect(form.isValid()).toBeTrue();
      expect(form.getFullConfig()).toEqual(jasmine.objectContaining({
        dest_sourceConnectionId: 'hapi', dest_dryRun: 'false', dest_testAsVendor: 'Epic', dest_ehrVendor: 'Epic',
      }));
    });

    it('falls back to Dry run when the run mode is not one the vendor offers', () => {
      const { form, pick, mode } = create({ vendor: null });
      pick('hapi');
      mode('test');

      expect(form.runMode()).toBe('dryRun');
    });
  });

  describe('validity', () => {
    it('isValid covers the connection; optionsValid the options; getMetadata needs both', () => {
      const { form, pick } = create({ vendor: 'Epic' });
      expect(form.isValid()).toBeFalse();

      pick('epic');
      expect(form.isValid()).toBeTrue();
      expect(form.optionsValid()).toBeTrue();
      expect(form.getMetadata()).not.toBeNull();

      form.form.controls.maxWritesPerRun.setValue('0');
      expect(form.isValid()).toBeTrue();
      expect(form.optionsValid()).toBeFalse();
      expect(form.getMetadata()).toBeNull();
    });

    it('is not valid with a connection the run mode does not offer', () => {
      const { form } = create({ vendor: 'Epic' });
      form.form.controls.sourceConnectionId.setValue('ath');
      expect(form.isValid()).toBeFalse();
    });
  });

  it('saves the holder encounter while testing as eClinicalWorks', () => {
    const { form, pick, mode } = create({ vendor: 'Healow' });
    mode('test');
    pick('hapi');
    form.form.controls.createHolderEncounter.setValue(true);

    expect(form.effective()!.offersHolderEncounter).toBeTrue();
    expect(form.getFullConfig()['dest_createHolderEncounter']).toBe('true');
  });

  describe('sections', () => {
    it('the connection half shows no option controls, and the options half no connection picker', () => {
      const { fixture, el, pick } = create({ vendor: 'Athenahealth' });
      pick('ath');
      expect(el.querySelector('#dw-ewb-target')).not.toBeNull();
      expect(el.querySelector('#dw-ewb-max')).toBeNull();

      fixture.componentRef.setInput('section', 'options');
      fixture.detectChanges();
      expect(el.querySelector('#dw-ewb-target')).toBeNull();
      expect(el.querySelector('#dw-ewb-max')).not.toBeNull();
      expect(el.querySelector('#dw-ewb-department')).not.toBeNull();
    });

    it("shows an athena connection's department as the default the node field overrides", () => {
      const { fixture, el, form, pick } = create({ vendor: 'Athenahealth' });
      pick('ath');
      fixture.componentRef.setInput('section', 'options');
      fixture.detectChanges();

      const input = el.querySelector<HTMLInputElement>('#dw-ewb-department')!;
      expect(input.placeholder).toBe('Connection default: 150');
      expect(form.getFullConfig()['dest_targetDepartmentId']).toBe('');

      form.patchFrom({ dest_sourceConnectionId: 'ath', dest_targetDepartmentId: '7' });
      fixture.detectChanges();
      expect(el.querySelector<HTMLInputElement>('#dw-ewb-department')!.value).toBe('7');
      expect(form.getFullConfig()['dest_targetDepartmentId']).toBe('7');
    });

    it('tells an athena test run to set the department here', () => {
      const { fixture, el, pick, mode } = create({ vendor: 'Athenahealth' });
      mode('test');
      pick('hapi');
      fixture.componentRef.setInput('section', 'options');
      fixture.detectChanges();

      expect(el.querySelector('[data-testid="ewb-athena-test-hint"]')).not.toBeNull();
    });
  });

  it('works out the opt-ins the chosen resource types still need', () => {
    const { fixture, form, pick, mode } = create({ vendor: 'Epic' });
    mode('test');
    pick('hapi');
    fixture.componentRef.setInput('selectedResourceTypes', ['AllergyIntolerance', 'Procedure']);
    fixture.detectChanges();

    expect(form.missingOptIns()).toEqual([
      { resourceType: 'Procedure', variants: ['external-radiotherapy-summary'], holderEncounter: false },
    ]);

    form.toggleVariant('external-radiotherapy-summary');
    expect(form.missingOptIns()).toEqual([]);
  });

  describe('listing write connections', () => {
    it('without permission to view Source Connections, asks nothing and says so, with no New button', () => {
      const { el, getAll } = create({ vendor: 'Epic', granted: ['ehrwriteback.create', 'epic.create'] });

      expect(getAll).not.toHaveBeenCalled();
      expect(el.textContent).toContain('You need permission to view Source Connections');
      expect(el.querySelector('[data-testid="ewb-new-connection"]')).toBeNull();
    });

    it('a 403 says the same', () => {
      const { el } = create({ vendor: 'Epic', getAll: () => throwError(() => new HttpErrorResponse({ status: 403 })) });
      expect(el.textContent).toContain('You need permission to view Source Connections');
      expect(el.querySelector('[data-testid="ewb-new-connection"]')).toBeNull();
    });

    it('any other failure says loading failed', () => {
      const { el } = create({ vendor: 'Epic', getAll: () => throwError(() => new HttpErrorResponse({ status: 500 })) });
      expect(el.textContent).toContain('Loading write connections failed. Try again.');
    });

    it('with no connection yet, points to New connection and Destination Connections', () => {
      const { el, mode } = create({ vendor: 'Athenahealth', connections: [] });
      expect(el.textContent).toContain('No athenahealth write connection yet. Click New connection, or add one under Destination Connections.');
      expect(el.querySelector('[data-testid="ewb-new-connection"]')).not.toBeNull();

      mode('test');
      expect(el.textContent).toContain('No FHIR test server that can stand in for athenahealth yet.');
    });
  });

  it('gives the Review step where it writes and how', () => {
    const { form, pick, mode } = create({ vendor: 'Epic' });
    pick('epic');
    mode('live');

    expect(form.reviewLines()).toEqual({
      writesTo: 'epic (Epic)',
      mode: 'Live: writes into Epic, up to 500 records per run',
    });
  });

  describe('a connection created on the go', () => {
    it('is reloaded and picked when it fits', () => {
      let connections = [HAPI];
      const { form, fixture, getAll } = create({ vendor: 'Epic', getAll: () => of(connections) });
      connections = [HAPI, EPIC];

      form.onCreated(EPIC);
      fixture.detectChanges();

      expect(getAll).toHaveBeenCalledTimes(2);
      expect(form.form.controls.sourceConnectionId.value).toBe('epic');
    });

    it('says so when it cannot take writes yet', () => {
      const { form, fixture, notice } = create({ vendor: 'Epic', connections: [HAPI] });

      form.onCreated(conn('new-epic', 'Epic', { access: 'Read' }));
      fixture.detectChanges();

      expect(form.form.controls.sourceConnectionId.value).toBe('');
      expect(notice()).toContain('cannot take writes yet');
    });
  });
});
