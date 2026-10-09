import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { of, throwError } from 'rxjs';
import { DestinationWizardComponent } from './destination-wizard.component';
import { CanvasNode } from '../../../models/node-v2.model';
import { EhrWriteCapabilitiesService, EhrWriteCapability } from '../../../services/ehr-write-capabilities.service';
import { ISourceConnectionService } from '../../../source-connections/services/i-source-connection.service';
import { ToastService } from '../../../services/toast.service';
import { DestinationConfigurationService } from '../../../destination-connections/services/destination-configuration.service';
import { DestinationConfigurationDto } from '../../../models/destination-configuration-v2.model';
import { WizardDestinationFormApi } from './destination-forms/destination-form-api';
import { ehrReviewLines } from './destination-forms/ehr-write-back/ehr-write-back.model';
import { EhrWriteTarget } from './ehr-write-type-grid/ehr-write-type-grid.model';
import { EhrWriteKindSwitches } from './ehr-write-type-grid/ehr-write-kinds.model';
import { ECW_CAPABILITIES, EPIC_CAPABILITIES } from './ehr-write-type-grid/ehr-write-capabilities.fixtures';
import { AddTransformEvent } from '../node-library-dialog.component';

/** A stand-in for the EHR Write-Back form, so the wizard's own Step 3 / Review logic is tested on its own. */
function fakeEhrForm(overrides: Partial<Record<string, unknown>> = {}) {
  const config: Record<string, string> = {
    dest_name: 'Epic write', dest_sourceConnectionId: 'c1', dest_ehrVendor: 'Epic', dest_dryRun: 'true',
    dest_testAsVendor: '', dest_maxWritesPerRun: '200', dest_enabledVariants: '', dest_createHolderEncounter: 'false',
  };
  const nameControl = new FormControl<string | null>('');
  return {
    kind: 'ehrWriteBack',
    targetVendor: signal('Epic'),
    writableResourceTypes: signal(['AllergyIntolerance']),
    runMode: signal('dryRun'),
    connectionName: () => 'Epic prod',
    nameControl: () => nameControl,
    liveEhrLabel: (): string | null => null,
    // As the real form does: the kinds ticked on Step 2 become the saved switches.
    setWriteKinds: (kinds: EhrWriteKindSwitches) => {
      config['dest_enabledVariants'] = kinds.enabledVariants.join(',');
      config['dest_createHolderEncounter'] = kinds.createHolderEncounter ? 'true' : 'false';
    },
    optIns: () => ({ enabledVariants: [], createHolderEncounter: false, createPatientIfMissing: false }),
    optionsValid: () => true,
    reviewLines: () => ehrReviewLines(config, 'Epic', 'Epic prod'),
    isValid: () => true,
    getRawValue: () => ({ name: 'Epic write' }),
    getFullConfig: () => config,
    getMetadata: () => ({ fields: config, secret: '' }),
    patchFrom: () => undefined,
    reset: () => undefined,
    config,
    ...overrides,
  };
}

const destination = (id: string, metadata: Record<string, string>): DestinationConfigurationDto =>
  ({
    id, name: id, destinationType: 'EhrWriteBack', keyVaultName: 'kv', secretName: 's', target: null, isEnabled: true,
    connectionMetadataJson: JSON.stringify(metadata),
  }) as unknown as DestinationConfigurationDto;

/**
 * The EHR Write-Back wizard: Connection → Resource types → Options → Review. Options (the form's second half) come
 * after the resource types, and leaving them re-saves the destination record so it holds the real options.
 */
describe('DestinationWizardComponent — EHR Write-Back', () => {
  let fixture: ComponentFixture<DestinationWizardComponent>;
  let wizard: DestinationWizardComponent;
  let destinations: jasmine.SpyObj<DestinationConfigurationService>;
  let capabilities: { forVendor: jasmine.Spy; writableResourceTypes: jasmine.Spy };

  const create = (destType: string, extra: Record<string, unknown> = {}) => {
    fixture = TestBed.createComponent(DestinationWizardComponent);
    wizard = fixture.componentInstance;
    fixture.componentRef.setInput('destType', destType);
    fixture.componentRef.setInput('attachNode', { id: 'src', x: 0, y: 0, connected: true, fields: {} } as CanvasNode);
    for (const [key, value] of Object.entries(extra)) fixture.componentRef.setInput(key, value);
  };
  const useForm = (form: ReturnType<typeof fakeEhrForm>) =>
    spyOn(wizard, 'activeForm').and.returnValue(form as unknown as WizardDestinationFormApi);

  beforeEach(() => {
    destinations = jasmine.createSpyObj<DestinationConfigurationService>('DestinationConfigurationService',
      ['getPaged', 'hasExecutionHistory', 'create', 'update']);
    destinations.getPaged.and.returnValue(of({ items: [], totalCount: 0, page: 1, pageSize: 100 }) as never);
    destinations.hasExecutionHistory.and.returnValue(of({ hasExecutionHistory: false }) as never);
    destinations.update.and.callFake((id: string) => of({ id, keyVaultName: 'kv', secretName: 's' }) as never);
    destinations.create.and.returnValue(of({ id: 'new', keyVaultName: 'kv', secretName: 's' }) as never);
    capabilities = {
      forVendor: jasmine.createSpy('forVendor').and.callFake((vendor: string) =>
        of({ vendor, supportsPatientMatch: true, cloneModeEnabled: false, capabilities: [] })),
      writableResourceTypes: jasmine.createSpy('writableResourceTypes').and.returnValue(of(['AllergyIntolerance'])),
    };

    TestBed.configureTestingModule({
      imports: [DestinationWizardComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: EhrWriteCapabilitiesService, useValue: capabilities },
        { provide: ISourceConnectionService, useValue: { getAll: () => of([]), getById: () => of({ vendorWriteApisActivated: false }) } },
        { provide: DestinationConfigurationService, useValue: destinations },
      ],
    });
  });

  it('names its steps Connection, Resource types, Options, Review; other destinations keep Map fields', () => {
    create('ehrwriteback');
    expect(wizard.stepLabels()).toEqual(['Connection', 'Resource types', 'Options', 'Review']);

    create('sql');
    expect(wizard.stepLabels()).toEqual(['Connection', 'Resource types', 'Map fields', 'Review']);
    expect(wizard.TOTAL_STEPS).toBe(4);
  });

  describe('form inputs', () => {
    it('passes the EHR inputs to the write-back form only', () => {
      create('sql');
      expect(Object.keys(wizard.activeFormInputs())).not.toContain('ehrVendor');
      expect(Object.keys(wizard.activeFormInputs())).not.toContain('section');

      create('ehrwriteback', { ehrVendor: 'Athenahealth', sourceIsTabular: true });
      wizard.selectedResources.set(['AllergyIntolerance']);
      const inputs = wizard.activeFormInputs();
      expect(inputs['ehrVendor']).toBe('Athenahealth');
      expect(inputs['section']).toBe('connection');
      // The kinds chosen on Step 2 go through setWriteKinds, not inputs.
      expect(Object.keys(inputs)).not.toContain('selectedResourceTypes');
      expect(Object.keys(inputs)).not.toContain('sourceIsTabular');
    });

    it('binds only the inputs the loaded form declares', () => {
      create('sql');
      expect(Object.keys(wizard.activeFormInputs())).toContain('destinationType');
      expect(Object.keys(wizard.boundFormInputs()).sort()).toEqual(['existingDestinationId', 'reusingExisting']);

      create('fabricwarehouse');
      expect(wizard.boundFormInputs()['destinationType']).toBe('DataFabricWarehouse');

      create('ehrwriteback', { ehrVendor: 'Epic' });
      expect(Object.keys(wizard.boundFormInputs()).sort())
        .toEqual(['ehrVendor', 'section']);
    });

    it('shows the options half on Step 3, but not for a chain node', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      wizard.step.set(3);
      expect(wizard.activeFormInputs()['section']).toBe('options');

      create('ehrwriteback', { ehrVendor: 'Epic', chainNodeEntry: true });
      wizard.step.set(3);
      expect(wizard.activeFormInputs()['section']).toBe('connection');
    });
  });

  describe('Step 3 (Options)', () => {
    it('Next waits for valid options only', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      const form = fakeEhrForm();
      useForm(form);
      wizard.step.set(3);
      expect(wizard.isNextDisabled()).toBeFalse();

      form.optionsValid = () => false;
      expect(wizard.isNextDisabled()).toBeTrue();
    });

    it('a new write-back is saved once, on leaving Options, with the chosen options (create right only)', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      const form = fakeEhrForm();
      useForm(form);

      wizard.next();
      expect(wizard.step()).toBe(2);
      expect(destinations.create).not.toHaveBeenCalled();
      expect(destinations.update).not.toHaveBeenCalled();

      form.config['dest_dryRun'] = 'false';
      wizard.step.set(3);
      wizard.next();

      expect(destinations.update).not.toHaveBeenCalled();
      expect(destinations.create).toHaveBeenCalledTimes(1);
      const [request] = destinations.create.calls.mostRecent().args;
      expect(request.destinationType).toBe('EhrWriteBack');
      expect(JSON.parse(request.connectionMetadataJson!)['dest_dryRun']).toBe('false');
      expect(wizard.resolvedDestinationId()).toBe('new');
      expect(wizard.step()).toBe(4);
    });

    it('a reopened write-back updates its own record on leaving Options', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      useForm(fakeEhrForm());
      wizard.resolvedDestinationId.set('dest-1');
      wizard.step.set(3);

      wizard.next();

      expect(destinations.create).not.toHaveBeenCalled();
      expect(destinations.update.calls.mostRecent().args[0]).toBe('dest-1');
      expect(wizard.step()).toBe(4);
    });

    it('stays on Options when re-saving the destination fails, saying it was not updated', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      useForm(fakeEhrForm());
      destinations.update.and.returnValue(throwError(() => new Error('x')));
      const show = spyOn(TestBed.inject(ToastService), 'show');
      wizard.resolvedDestinationId.set('dest-1');
      wizard.step.set(3);

      wizard.next();

      expect(wizard.step()).toBe(3);
      expect(show).toHaveBeenCalledWith('Destination not updated', 'x');
    });

    it('Next does nothing while getMetadata() is null', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      useForm(fakeEhrForm({ getMetadata: () => null }));
      wizard.resolvedDestinationId.set('dest-1');
      wizard.step.set(3);

      wizard.next();

      expect(destinations.update).not.toHaveBeenCalled();
      expect(destinations.create).not.toHaveBeenCalled();
      expect(wizard.step()).toBe(3);
    });

    it('copying a saved destination: Next leaves that record alone; changed options fork a new one at save', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      const form = fakeEhrForm({ getRawValue: () => ({ name: 'Epic write', maxWritesPerRun: '10' }) });
      useForm(form);
      wizard.existingOptions.set([destination('saved-1', { dest_ehrVendor: 'Epic' })]);
      wizard.connectionMode.set('existing');
      wizard.selectedExistingId.set('saved-1');
      (wizard as unknown as { _existingBaseline: Record<string, unknown> })._existingBaseline = { name: 'Epic write', maxWritesPerRun: '500' };
      wizard.selectedResources.set(['AllergyIntolerance']);
      wizard.step.set(3);

      wizard.next();
      expect(destinations.update).not.toHaveBeenCalled();
      expect(destinations.create).not.toHaveBeenCalled();
      expect(wizard.step()).toBe(4);

      let saved: AddTransformEvent | null = null;
      wizard.saved.subscribe(e => (saved = e));
      (wizard as unknown as { _save(): void })._save();

      expect(saved!.transformId).toBe('dest-ehr-writeback');
      expect(saved!.config!['dest_inheritSecretFromDestinationId']).toBe('saved-1');
      expect(saved!.config!['destinationResolved']).toBeUndefined();
    });
  });

  describe('kinds of record on Step 2', () => {
    const loadTarget = (vendor: string, caps: EhrWriteCapability[], fields: Record<string, string> = {}) => {
      capabilities.forVendor.and.callFake((v: string) =>
        of({ vendor: v, supportsPatientMatch: true, cloneModeEnabled: false, capabilities: caps }));
      (wizard as unknown as { _loadEhrWriteTarget(f: Record<string, string>): void })._loadEhrWriteTarget({
        dest_ehrVendor: vendor, dest_sourceConnectionId: 'c1', ...fields,
      });
    };
    const kind = (resourceType: string, kindId: string) => wizard.onKindToggled({ resourceType, kindId });
    const saved = () => wizard.ehrSavedKinds()!;

    it('ticking an optional kind ticks its type; unticking the last one unticks it', () => {
      create('ehrwriteback', { ehrVendor: 'Epic', sourceResources: ['Observation', 'BodyStructure'] });
      loadTarget('Epic', EPIC_CAPABILITIES);

      kind('Observation', 'lines-drains-airways');
      expect(wizard.selectedResources()).toEqual(['Observation']);
      expect(saved().enabledVariants).toEqual(['lines-drains-airways']);

      // The always-included kind is locked on while the type is ticked.
      kind('Observation', 'vital-signs');
      expect(wizard.selectedResources()).toEqual(['Observation']);

      kind('Observation', 'lines-drains-airways');
      expect(wizard.selectedResources()).toEqual(['Observation']);
      expect(saved().enabledVariants).toEqual([]);

      kind('BodyStructure', 'radiotherapy-volume');
      expect(wizard.selectedResources()).toEqual(['Observation', 'BodyStructure']);
      kind('BodyStructure', 'radiotherapy-volume');
      expect(wizard.selectedResources()).toEqual(['Observation']);
      expect(saved().enabledVariants).toEqual([]);
    });

    it('ticking a type written only through optional kinds ticks them; ticking one with an always-included kind does not', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      loadTarget('Epic', EPIC_CAPABILITIES);

      wizard.toggleResource('BodyStructure');
      wizard.toggleResource('Observation');
      expect(saved().enabledVariants).toEqual(['radiotherapy-volume']);

      wizard.toggleResource('BodyStructure');
      expect(saved().enabledVariants).toEqual([]);
      expect(wizard.ehrKindSwitches().enabledVariants).toEqual([]);
    });

    it('Procedure and ServiceRequest share one switch: unticking one type keeps the other\'s summaries', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      loadTarget('Epic', EPIC_CAPABILITIES);
      wizard.toggleResource('Procedure');
      wizard.toggleResource('ServiceRequest');

      kind('Procedure', 'external-radiotherapy-summary');

      expect(wizard.selectedResources()).toEqual(['ServiceRequest']);
      expect(saved().enabledVariants).toEqual(['external-radiotherapy-summary']);
    });

    it('an eClinicalWorks history kind turns the telephone encounter on by itself', () => {
      create('ehrwriteback', { ehrVendor: 'Healow' });
      loadTarget('Healow', ECW_CAPABILITIES);
      wizard.toggleResource('Condition');
      expect(saved().createHolderEncounter).toBeFalse();

      kind('Condition', 'medical-history');
      expect(saved()).toEqual({ enabledVariants: [], createHolderEncounter: true });

      // One switch for both: Surgical history is ticked too once Procedure is, and unticking Medical history unticks
      // Procedure, whose only kind it shared.
      wizard.toggleResource('Procedure');
      expect(wizard.ehrReviewTypeLines()).toEqual([
        { resourceType: 'Condition', kinds: ['Problem list', 'Encounter diagnosis', 'Medical history'] },
        { resourceType: 'Procedure', kinds: ['Surgical history'] },
      ]);
      kind('Condition', 'medical-history');
      expect(wizard.selectedResources()).toEqual(['Condition']);
      expect(saved().createHolderEncounter).toBeFalse();
    });

    it('hands the kinds to the form, and saves what the old Options list saved for the same choices', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      const form = fakeEhrForm();
      useForm(form);
      loadTarget('Epic', EPIC_CAPABILITIES);

      wizard.toggleResource('Observation');
      kind('Observation', 'lines-drains-airways');
      kind('DocumentReference', 'document-information');
      wizard.toggleResource('Procedure');
      wizard.step.set(3);
      wizard.next();

      const [request] = destinations.create.calls.mostRecent().args;
      const fields = JSON.parse(request.connectionMetadataJson!) as Record<string, string>;
      // The old flow: Observation, DocumentReference and Procedure ticked, then these three APIs ticked under Options.
      expect(fields['dest_enabledVariants'].split(',').sort())
        .toEqual(['document-information', 'external-radiotherapy-summary', 'lines-drains-airways']);
      expect(fields['dest_createHolderEncounter']).toBe('false');
    });

    it('a reopened destination starts with its saved kinds; a CSV / SQL-only one is dropped for an EHR source', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      wizard.selectedResources.set(['Observation']);
      loadTarget('Epic', EPIC_CAPABILITIES, { dest_enabledVariants: 'lines-drains-airways,dicom-image-characteristics' });

      expect(wizard.ehrReviewTypeLines()).toEqual([
        { resourceType: 'Observation', kinds: ['Vital signs', 'Lines, drains and airways'] },
      ]);
      expect(saved().enabledVariants).toEqual(['lines-drains-airways']);
    });

    it('Step 2 waits for a reopened type with no kind ticked; Options never waits for a kind', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      wizard.selectedResources.set(['Procedure']);
      loadTarget('Epic', EPIC_CAPABILITIES);
      wizard.step.set(2);
      expect(wizard.ehrTypesWithoutKind()).toEqual(['Procedure']);
      expect(wizard.isNextDisabled()).toBeTrue();

      kind('Procedure', 'external-radiotherapy-summary');
      expect(wizard.isNextDisabled()).toBeFalse();

      useForm(fakeEhrForm());
      wizard.selectedResources.set(['Procedure']);
      wizard.ehrKindSwitches.set({ enabledVariants: [], createHolderEncounter: false });
      wizard.step.set(3);
      expect(wizard.isNextDisabled()).toBeFalse();
    });

    it('a greyed selected type with no kind never holds Step 2 (its card shows no kinds to tick)', () => {
      create('ehrwriteback', { ehrVendor: 'Epic', sourceResources: ['Observation'] });
      wizard.selectedResources.set(['Observation', 'BodyStructure']);
      loadTarget('Epic', EPIC_CAPABILITIES);
      wizard.step.set(2);

      const body = wizard.ehrWriteTypeRows()!.find((r) => r.resourceType === 'BodyStructure')!;
      expect(body.selectable).toBeFalse();
      expect(body.kinds).toEqual([]);
      expect(wizard.ehrTypesWithoutKind()).toEqual([]);
      expect(wizard.isNextDisabled()).toBeFalse();
    });

    it('eClinicalWorks: a greyed Procedure with the telephone encounter off neither holds Step 2 nor is unticked by a kind', () => {
      create('ehrwriteback', { ehrVendor: 'Healow', sourceResources: ['Condition'] });
      wizard.selectedResources.set(['Condition', 'Procedure']);
      loadTarget('Healow', ECW_CAPABILITIES, { dest_createHolderEncounter: 'false' });
      wizard.step.set(2);
      expect(wizard.ehrTypesWithoutKind()).toEqual([]);
      expect(wizard.isNextDisabled()).toBeFalse();

      kind('Condition', 'medical-history');
      kind('Condition', 'medical-history');
      // The greyed card stays as it was, for the user to untick.
      expect(wizard.selectedResources()).toEqual(['Condition', 'Procedure']);
    });

    it('Review lists the kinds under the resource types', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      fixture.detectChanges();
      loadTarget('Epic', EPIC_CAPABILITIES);
      wizard.toggleResource('AllergyIntolerance');
      kind('Observation', 'lines-drains-airways');
      wizard.step.set(4);
      fixture.detectChanges();

      const lines = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="ewb-review-type"]'))
        .map((l) => l.textContent!.trim());
      expect(lines).toEqual(['AllergyIntolerance', 'Observation: Vital signs, Lines, drains and airways']);
    });
  });

  it('Step 1 has no "copy settings from a saved destination" box: only the form Connection', () => {
    create('ehrwriteback', { ehrVendor: 'Epic' });
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('#dw-existing')).toBeNull();
    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('Copy settings from');
  });

  describe('saved destinations', () => {
    it('a new write-back asks for no saved destinations: Step 1 is only its connection', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      (wizard as unknown as { _loadExistingOptions(): void })._loadExistingOptions();

      expect(destinations.getPaged).not.toHaveBeenCalled();
    });

    it('a node saved from a picked saved destination still loads it, asking for EHR write-back destinations only', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      wizard.connectionMode.set('existing');
      (wizard as unknown as { _loadExistingOptions(): void })._loadExistingOptions();

      expect(destinations.getPaged.calls.mostRecent().args[0]).toEqual(
        jasmine.objectContaining({ destinationType: 'EhrWriteBack', isEnabled: true, page: 1, pageSize: 100 }));
    });
  });

  describe('Review', () => {
    it('shows the lines the form gives, and none before the form exists', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      expect(wizard.ehrReviewLines()).toBeNull();

      useForm(fakeEhrForm({ reviewLines: () => ({ writesTo: 'w', mode: 'm' }) }));
      expect(wizard.ehrReviewLines()).toEqual({ writesTo: 'w', mode: 'm' });
    });

    it('lets the user name the destination on Review, defaulting to the connection name', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      expect(wizard.reviewName()).toBeNull();
      const form = fakeEhrForm();
      useForm(form);
      const name = wizard.reviewName()!;
      expect(name.placeholder).toBe('Epic prod');
      expect(name.control).toBe(form.nameControl());
    });

    it('warns in red only for a live run into an EHR', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      useForm(fakeEhrForm());
      expect(wizard.ehrLiveLabel()).toBeNull();

      create('ehrwriteback', { ehrVendor: 'Epic' });
      useForm(fakeEhrForm({ liveEhrLabel: () => 'Epic' }));
      wizard.step.set(4);
      fixture.detectChanges();
      expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="ewb-live-warning"]')?.textContent?.trim())
        .toBe('Live: records will be written into Epic. This cannot be undone.');
    });

    it('names the destination type by the EHR of the tile', () => {
      create('ehrwriteback', { ehrVendor: 'Healow' });
      expect(wizard.reviewDestLabel()).toBe('EHR write-back — eClinicalWorks');

      create('ehrwriteback', { ehrVendor: 'GenericFhir' });
      expect(wizard.reviewDestLabel()).toBe('EHR write-back — FHIR test server');

      create('sql');
      expect(wizard.reviewDestLabel()).toBe('SQL Server');
    });

    it('without a tile, a test run is named by the EHR it stands in for, not the test server', () => {
      create('ehrwriteback');
      const form = fakeEhrForm();
      Object.assign(form.config, { dest_ehrVendor: 'GenericFhir', dest_testAsVendor: 'Athenahealth', dest_dryRun: 'false' });
      useForm(form);
      expect(wizard.reviewDestLabel()).toBe('EHR write-back — athenahealth');
    });
  });

  describe('reopening a saved node', () => {
    const LEGACY_TEST = {
      dest_ehrVendor: 'GenericFhir', dest_testAsVendor: 'Epic', dest_dryRun: 'false', dest_sourceConnectionId: 'hapi',
      dest_resources: 'AllergyIntolerance',
    };
    const reopen = (fields: Record<string, string>, ehrVendor: string | null) => {
      create('ehrwriteback', { ehrVendor });
      (wizard as unknown as { _populateFromNode(n: CanvasNode): void })._populateFromNode(
        { id: 'n1', x: 0, y: 0, connected: true, transformId: 'dest-ehr-writeback', fields } as unknown as CanvasNode);
    };
    const target = () => (wizard as unknown as { ehrWriteTarget(): EhrWriteTarget | null }).ehrWriteTarget();

    it('reopening a legacy test run saved with dest_ehrVendor GenericFhir loads Epic\'s writable types', () => {
      reopen(LEGACY_TEST, 'Epic');

      expect(capabilities.writableResourceTypes).toHaveBeenCalledWith('Epic');
      expect(capabilities.forVendor).toHaveBeenCalledWith('Epic');
      expect(capabilities.forVendor).not.toHaveBeenCalledWith('GenericFhir');
      expect(target()!.vendor).toBe('Epic');
      expect(target()!.vendorWriteApisActivated).toBeTrue();
    });

    it('without a tile, finds the tested vendor the same way the Node Library does', () => {
      reopen(LEGACY_TEST, null);

      expect(capabilities.writableResourceTypes).toHaveBeenCalledWith('Epic');
      expect(target()!.vendor).toBe('Epic');
    });

    it('a dry run over a test server stands in for the EHR with everything activated, as a test run does', () => {
      reopen({ ...LEGACY_TEST, dest_dryRun: 'true' }, 'Epic');

      expect(capabilities.writableResourceTypes).toHaveBeenCalledWith('Epic');
      expect(target()!.vendorWriteApisActivated).toBeTrue();
    });
  });

  describe('a chain node in front of a write-back', () => {
    it('saves the write-back\'s own settings exactly as the node had them', () => {
      const savedFields = {
        dest_name: 'Epic write', dest_sourceConnectionId: 'hapi', dest_ehrVendor: 'Epic', dest_dryRun: 'true',
        dest_testAsVendor: 'Epic', dest_maxWritesPerRun: '200', dest_enabledVariants: '',
        destinationId: 'dest-1', destinationResolved: 'true',
      };
      create('ehrwriteback', {
        ehrVendor: 'Epic', chainNodeEntry: true, initialStep: 3, initialConfigTab: 'transformation',
        editNode: { id: 'n1', x: 0, y: 0, connected: true, transformId: 'dest-ehr-writeback', fields: savedFields },
      });
      // The hidden form could not check the connection (e.g. no right to list connections) and reports a dry run.
      const form = fakeEhrForm();
      Object.assign(form.config, { dest_sourceConnectionId: '', dest_testAsVendor: '', dest_dryRun: 'true' });
      useForm(form);

      let saved: AddTransformEvent | null = null;
      wizard.saved.subscribe(e => (saved = e));
      (wizard as unknown as { _save(): void })._save();

      expect(saved!.config).toEqual(jasmine.objectContaining({
        dest_sourceConnectionId: 'hapi', dest_testAsVendor: 'Epic', dest_dryRun: 'true', dest_ehrVendor: 'Epic',
      }));
    });
  });

  describe('rendered', () => {
    it('switching to another EHR tile starts over on Step 1', () => {
      create('ehrwriteback', { ehrVendor: 'Epic' });
      fixture.detectChanges();
      wizard.step.set(2);
      wizard.selectedResources.set(['AllergyIntolerance']);
      wizard.ehrWritableResourceTypes.set(['AllergyIntolerance']);
      wizard.connectionMode.set('existing');
      wizard.selectedExistingId.set('saved-1');

      fixture.componentRef.setInput('ehrVendor', 'Athenahealth');
      fixture.detectChanges();

      expect(wizard.step()).toBe(1);
      expect(wizard.selectedResources()).toEqual([]);
      expect(wizard.ehrWritableResourceTypes()).toBeNull();
      expect(wizard.selectedExistingId()).toBeNull();
      expect(wizard.connectionMode()).toBe('new');
    });

    it('a chain node in front of a write-back shows Step 3\'s information, not the Options form', () => {
      create('ehrwriteback', { ehrVendor: 'Epic', chainNodeEntry: true, initialStep: 3 });
      wizard.step.set(3);
      fixture.detectChanges();
      const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

      expect(text).toContain('Shaped by the EHR\'s write rules');
      expect(text).not.toContain('Options — how records are written');
    });

    it('Step 3 shows the Options heading for the tile\'s EHR', () => {
      create('ehrwriteback', { ehrVendor: 'Athenahealth' });
      wizard.step.set(3);
      fixture.detectChanges();
      const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

      expect(text).toContain('Options — how records are written to athenahealth');
      expect(text).not.toContain('Shaped by the EHR\'s write rules');
    });
  });
});
