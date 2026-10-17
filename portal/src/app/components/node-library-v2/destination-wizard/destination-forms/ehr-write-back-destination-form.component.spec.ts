import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { NEVER, Observable, Subject, of, throwError } from 'rxjs';
import { EhrWriteBackDestinationFormComponent } from './ehr-write-back-destination-form.component';
import { ISourceConnectionService } from '../../../../source-connections/services/i-source-connection.service';
import { SourceConnectionModel } from '../../../../source-connections/models/source-connection.model';
import { EhrWriteCapabilities, EhrWriteCapabilitiesService, EhrWriteCapability } from '../../../../services/ehr-write-capabilities.service';
import { PermissionService } from '../../../../auth/services/permission.service';
import { EhrWriteVendor } from './ehr-write-back/ehr-write-back.model';

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
  /** The EhrWriteBack:DryRunEnabled setting as the capabilities API reports it. Most cases run with it on (the
   *  default here); 'omitted' sends no value at all, as an older API or a failed call does. */
  dryRunEnabled?: boolean | 'omitted';
}

/**
 * The EHR Write-Back form: Step 1 is only the Connection (the EHR's own write connections and its FHIR test servers);
 * the connection chosen decides the run, Dry run is an option, and the saved keys stay dest_dryRun + dest_testAsVendor.
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
              (options.pendingVendors ?? []).includes(vendor)
                ? NEVER
                : of(options.dryRunEnabled === 'omitted' ? BY_VENDOR[vendor] : { ...BY_VENDOR[vendor], dryRunEnabled: options.dryRunEnabled ?? true }),
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
    const options_ = () => {
      fixture.componentRef.setInput('section', 'options');
      fixture.detectChanges();
    };
    const optionIds = () => Array.from(el.querySelectorAll<HTMLOptionElement>('#dw-ewb-target option')).map(o => o.value).filter(Boolean);
    const notice = () => el.querySelector('[data-testid="ewb-connection-notice"]')?.textContent?.trim() ?? null;
    const byTestId = (id: string) => el.querySelector(`[data-testid="${id}"]`);
    return { fixture, form, el, getAll, pick, showOptions: options_, optionIds, notice, byTestId };
  }

  describe('Step 1 is only the connection', () => {
    it('shows no name, run mode, copy box or capability text: just the Connection dropdown', () => {
      const { el } = create({ vendor: 'Epic' });
      expect(el.querySelector('#dw-ewb-name')).toBeNull();
      expect(el.querySelector('input[type="radio"]')).toBeNull();
      expect(el.querySelector('.dw-callout--info')).toBeNull();
      expect(el.querySelector('#dw-ewb-target')).not.toBeNull();
    });

    it('offers the EHR\'s own connections and the test servers that stand in for it, choosing none', () => {
      const { form, optionIds } = create({ vendor: 'Epic' });
      expect(optionIds()).toEqual(['epic', 'hapi']);
      expect(form.form.controls.sourceConnectionId.value).toBe('');
      expect(form.runMode()).toBeNull();
      expect(form.isValid()).toBeFalse();
    });

    it('never chooses the only live EHR connection for the user', () => {
      const { form } = create({ vendor: 'Epic', connections: [EPIC] });
      expect(form.form.controls.sourceConnectionId.value).toBe('');
    });
  });

  describe('the connection decides the run', () => {
    it('an EHR connection is a live run', () => {
      const { form, pick, byTestId } = create({ vendor: 'Epic' });
      pick('epic');
      expect(form.runMode()).toBe('live');
      expect(form.liveEhrLabel()).toBe('Epic');
      expect(byTestId('ewb-test-run-line')).toBeNull();
      expect(form.getFullConfig()).toEqual(jasmine.objectContaining({
        dest_dryRun: 'false', dest_testAsVendor: '', dest_ehrVendor: 'Epic', dest_sourceConnectionId: 'epic',
      }));
      expect(form.isValid()).toBeTrue();
    });

    it('a test server is a test run for the tile\'s EHR, with one line saying nothing reaches it', () => {
      const { form, pick, byTestId } = create({ vendor: 'Healow' });
      pick('hapi');
      expect(form.runMode()).toBe('test');
      expect(form.liveEhrLabel()).toBeNull();
      expect(byTestId('ewb-test-run-line')?.textContent?.trim()).toBe('Test run: nothing reaches eClinicalWorks.');
      expect(form.getFullConfig()).toEqual(jasmine.objectContaining({
        dest_dryRun: 'false', dest_testAsVendor: 'Healow', dest_ehrVendor: 'Healow', dest_sourceConnectionId: 'hapi',
      }));
      expect(form.writableResourceTypes()).toEqual(['AllergyIntolerance', 'Condition']);
      expect(form.isValid()).toBeTrue();
    });

    it('a test server is not valid until the tested EHR\'s capabilities load, and still records the tile', () => {
      const { form, pick } = create({ vendor: 'Epic', pendingVendors: ['Epic'] });
      pick('hapi');
      expect(form.isValid()).toBeFalse();
      expect(form.getFullConfig()['dest_ehrVendor']).toBe('Epic');
    });
  });

  describe('the name', () => {
    it('takes the chosen connection\'s name until the user changes it on Review', () => {
      const { form, pick } = create({ vendor: 'Healow' });
      pick('hapi');
      expect(form.getFullConfig()['dest_name']).toBe('Local HAPI');
      expect(form.connectionName()).toBe('Local HAPI');

      form.nameControl().setValue('  eCW nightly  ');
      expect(form.getFullConfig()['dest_name']).toBe('eCW nightly');
    });

    it('keeps a saved name', () => {
      const { form } = create({ vendor: 'Epic' });
      form.patchFrom({ dest_name: 'Epic allergies', dest_sourceConnectionId: 'epic', dest_dryRun: 'false' });
      expect(form.getFullConfig()['dest_name']).toBe('Epic allergies');
    });

    it('a saved name that was the connection\'s own follows a newly chosen connection', () => {
      const { form, pick } = create({ vendor: 'Healow' });
      form.patchFrom({ dest_name: 'ecw', dest_sourceConnectionId: 'ecw', dest_dryRun: 'false' });
      expect(form.getFullConfig()['dest_name']).toBe('ecw');

      pick('hapi');
      expect(form.getFullConfig()['dest_name']).toBe('Local HAPI');
    });

    it('a saved name the user typed stays when another connection is chosen', () => {
      const { form, pick } = create({ vendor: 'Healow' });
      form.patchFrom({ dest_name: 'eCW nightly', dest_sourceConnectionId: 'ecw', dest_dryRun: 'false' });

      pick('hapi');
      expect(form.getFullConfig()['dest_name']).toBe('eCW nightly');
    });

    it('decides about a saved name only once the connections have loaded', () => {
      const pending = new Subject<SourceConnectionModel[]>();
      const { form, pick } = create({ vendor: 'Healow', getAll: () => pending });
      form.patchFrom({ dest_name: 'ecw', dest_sourceConnectionId: 'ecw', dest_dryRun: 'false' });
      expect(form.nameControl().value).toBe('ecw');

      pending.next(ALL);
      pick('hapi');
      expect(form.getFullConfig()['dest_name']).toBe('Local HAPI');
    });
  });

  describe('Dry run is an option', () => {
    it('is offered under Options while the setting is on, and saves a dry run', () => {
      const { form, pick, showOptions, byTestId } = create({ vendor: 'Epic' });
      pick('epic');
      showOptions();
      const box = byTestId('ewb-dry-run') as HTMLInputElement;
      expect(box).not.toBeNull();
      expect(box.parentElement?.textContent).toContain('Dry run: check every record, send nothing');

      form.form.controls.dryRun.setValue(true);
      expect(form.runMode()).toBe('dryRun');
      expect(form.liveEhrLabel()).toBeNull();
      expect(form.getFullConfig()).toEqual(jasmine.objectContaining({ dest_dryRun: 'true', dest_testAsVendor: '' }));
    });

    it('a dry run over a test server keeps the tested EHR', () => {
      const { form, pick } = create({ vendor: 'Epic' });
      pick('hapi');
      form.form.controls.dryRun.setValue(true);
      expect(form.getFullConfig()).toEqual(jasmine.objectContaining({ dest_dryRun: 'true', dest_testAsVendor: 'Epic' }));
    });

    it('is not offered with the setting off (or a missing setting)', () => {
      for (const dryRunEnabled of [false, 'omitted'] as const) {
        TestBed.resetTestingModule();
        const { pick, showOptions, byTestId } = create({ vendor: 'Epic', dryRunEnabled });
        pick('epic');
        showOptions();
        expect(byTestId('ewb-dry-run')).withContext(String(dryRunEnabled)).toBeNull();
      }
    });

    it('a saved dry run stays ticked with a note while the setting is off', () => {
      const { form, fixture, showOptions, byTestId } = create({ vendor: 'Epic', dryRunEnabled: false });
      form.patchFrom({ dest_sourceConnectionId: 'epic', dest_dryRun: 'true' });
      fixture.detectChanges();
      showOptions();

      expect(form.runMode()).toBe('dryRun');
      expect((byTestId('ewb-dry-run') as HTMLInputElement).checked).toBeTrue();
      expect(byTestId('ewb-dry-run-off-note')).not.toBeNull();
      expect(form.getFullConfig()['dest_dryRun']).toBe('true');
    });

    it('a node saved without dest_dryRun is a dry run', () => {
      const { form, fixture } = create({ vendor: 'Epic' });
      form.patchFrom({ dest_sourceConnectionId: 'epic' });
      fixture.detectChanges();
      expect(form.runMode()).toBe('dryRun');
    });
  });

  describe('reopening a saved node', () => {
    it('restores a test run', () => {
      const { form, fixture } = create({ vendor: 'Healow' });
      form.patchFrom({ dest_sourceConnectionId: 'hapi', dest_dryRun: 'false', dest_testAsVendor: 'Healow', dest_ehrVendor: 'Healow' });
      fixture.detectChanges();
      expect(form.runMode()).toBe('test');
      expect(form.isValid()).toBeTrue();
    });

    it('without a tile, a saved test run is grouped for the EHR it stood in for', () => {
      const { form, fixture, optionIds } = create({ vendor: null });
      form.patchFrom({ dest_sourceConnectionId: 'hapi', dest_dryRun: 'false', dest_testAsVendor: 'Athenahealth' });
      fixture.detectChanges();
      expect(optionIds()).toEqual(['ath', 'hapi']);
      expect(form.getFullConfig()['dest_testAsVendor']).toBe('Athenahealth');
    });

    it('a saved live plain FHIR server write-back opens with a plain note and stays as saved', () => {
      const { form, fixture, byTestId } = create({ vendor: null });
      form.patchFrom({ dest_sourceConnectionId: 'hapi', dest_dryRun: 'false', dest_ehrVendor: 'GenericFhir' });
      fixture.detectChanges();
      expect(byTestId('ewb-plain-fhir-note')).not.toBeNull();
      expect(form.liveEhrLabel()).toBeNull();
      expect(form.isValid()).toBeTrue();
      expect(form.getFullConfig()).toEqual(jasmine.objectContaining({ dest_dryRun: 'false', dest_testAsVendor: '', dest_ehrVendor: 'GenericFhir' }));
    });

    it('reports a saved connection that is no longer available', () => {
      const { form, fixture, notice } = create({ vendor: 'Epic' });
      form.patchFrom({ dest_sourceConnectionId: 'gone', dest_dryRun: 'false' });
      fixture.detectChanges();
      expect(notice()).toContain('no longer available');
      expect(form.isValid()).toBeFalse();
    });

    it('reports a connection for another EHR', () => {
      const { form, fixture, notice } = create({ vendor: 'Epic' });
      form.patchFrom({ dest_sourceConnectionId: 'ath', dest_dryRun: 'false' });
      fixture.detectChanges();
      expect(notice()).toBe('That connection does not write to Epic. Choose another.');
    });
  });

  describe('sections', () => {
    it('the connection half shows no option controls, and the options half no connection picker', () => {
      const { el, pick, showOptions } = create({ vendor: 'Athenahealth' });
      pick('ath');
      expect(el.querySelector('#dw-ewb-max')).toBeNull();

      showOptions();
      expect(el.querySelector('#dw-ewb-target')).toBeNull();
      expect(el.querySelector('#dw-ewb-max')).not.toBeNull();
      expect(el.querySelector('#dw-ewb-department')).not.toBeNull();
    });

    it('tells an athena test run to set the department here', () => {
      const { el, pick, showOptions } = create({ vendor: 'Athenahealth' });
      pick('hapi');
      showOptions();
      expect(el.querySelector('[data-testid="ewb-athena-test-hint"]')).not.toBeNull();
    });

    it('the options half lists no vendor APIs: Step 2 chooses the kinds', () => {
      const { el, form, pick, showOptions } = create({ vendor: 'Healow' });
      pick('hapi');
      showOptions();
      expect(el.querySelector('[data-variant]')).toBeNull();
      expect(form.optionsValid()).toBeTrue();
      form.setWriteKinds({ enabledVariants: [], createHolderEncounter: true });
      expect(form.getFullConfig()['dest_createHolderEncounter']).toBe('true');
    });
  });

  describe('listing write connections', () => {
    it('without permission to view Source Connections, asks nothing and says so, with no New button', () => {
      const { el, getAll } = create({ vendor: 'Epic', granted: ['ehrwriteback.create', 'epic.create'] });
      expect(getAll).not.toHaveBeenCalled();
      expect(el.textContent).toContain('You need permission to view Source Connections');
      expect(el.querySelector('[data-testid="ewb-new-connection"]')).toBeNull();
    });

    it('a 500 says loading failed', () => {
      const { el } = create({ vendor: 'Epic', getAll: () => throwError(() => new HttpErrorResponse({ status: 500 })) });
      expect(el.textContent).toContain('Loading write connections failed. Try again.');
    });

    it('with no connection yet, points to New connection and Destination Connections', () => {
      const { el } = create({ vendor: 'Athenahealth', connections: [] });
      expect(el.textContent).toContain('No athenahealth write connection or test server yet.');
      expect(el.querySelector('[data-testid="ewb-new-connection"]')).not.toBeNull();
      expect(el.querySelector('[data-testid="ewb-new-test-server"]')).not.toBeNull();
    });
  });

  it('gives the Review step where it writes and how', () => {
    const { form, pick } = create({ vendor: 'Epic' });
    pick('epic');
    expect(form.reviewLines()).toEqual({ writesTo: 'epic (Epic)', mode: 'Live: writes into Epic, up to 500 records per run' });
    pick('hapi');
    expect(form.reviewLines()).toEqual({
      writesTo: 'Local HAPI (FHIR test server), shaped as Epic',
      mode: 'Test run: nothing reaches Epic, up to 500 records per run',
    });
  });

  describe('a connection created on the go', () => {
    it('a new test server is reloaded and picked', () => {
      let connections = [EPIC];
      const { form, fixture, getAll } = create({ vendor: 'Epic', getAll: () => of(connections) });
      connections = [EPIC, HAPI];
      form.onCreated(HAPI);
      fixture.detectChanges();
      expect(getAll).toHaveBeenCalledTimes(2);
      expect(form.form.controls.sourceConnectionId.value).toBe('hapi');
    });

    it('a new EHR connection is not picked: a live run is chosen on purpose', () => {
      let connections = [HAPI];
      const { form, fixture, notice } = create({ vendor: 'Epic', getAll: () => of(connections) });
      connections = [HAPI, EPIC];
      form.onCreated(EPIC);
      fixture.detectChanges();
      expect(form.form.controls.sourceConnectionId.value).toBe('');
      expect(notice()).toBe('The new connection was added. Choose it above to write into Epic.');
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
