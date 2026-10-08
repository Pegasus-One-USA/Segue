import { ComponentFixture, TestBed } from '@angular/core/testing';
import { EhrWriteCapability } from '../../../../services/ehr-write-capabilities.service';
import { EhrWriteTypeGridComponent } from './ehr-write-type-grid.component';
import {
  AWAITING_ACTIVATION_NOTE,
  HOLDER_ENCOUNTER_REASON,
  NEEDS_TABULAR_SOURCE_REASON,
  NOT_READ_BY_SOURCE_REASON,
  PATIENT_OPT_IN_NOTE,
  classifyEhrWriteTypes,
} from './ehr-write-type-grid.model';

const capability = (resourceType: string, extra: Partial<EhrWriteCapability> = {}): EhrWriteCapability => ({
  resourceType,
  operations: ['create'],
  vendorApiId: 'x',
  variant: null,
  requiresEncounter: false,
  optInOnly: false,
  allowedApplicationTypes: ['Backend'],
  liveWriteSupported: true,
  ...extra,
});

describe('classifyEhrWriteTypes', () => {
  const capabilities = [
    capability('AllergyIntolerance'),
    capability('Condition', { requiresVendorActivation: true }),
    capability('ServiceRequest', { requiresTargetReferences: true }),
  ];
  const classify = (overrides: { sourceIsTabular?: boolean; activated?: boolean } = {}) =>
    classifyEhrWriteTypes({
      candidates: ['AllergyIntolerance', 'Condition', 'ServiceRequest', 'Encounter'],
      capabilities,
      vendor: 'Healow',
      sourceIsTabular: overrides.sourceIsTabular ?? false,
      vendorWriteApisActivated: overrides.activated ?? false,
    });

  it('greys a type the EHR does not accept, naming the vendor', () => {
    const encounter = classify().find((r) => r.resourceType === 'Encounter')!;
    expect(encounter.selectable).toBeFalse();
    expect(encounter.reason).toBe('Not accepted by eClinicalWorks');
  });

  it('greys a type needing the target\'s own ids unless the source is a CSV / SQL Table source', () => {
    expect(classify().find((r) => r.resourceType === 'ServiceRequest')).toEqual(
      jasmine.objectContaining({ selectable: false, reason: NEEDS_TABULAR_SOURCE_REASON }),
    );
    expect(classify({ sourceIsTabular: true }).find((r) => r.resourceType === 'ServiceRequest')?.selectable).toBeTrue();
  });

  it('keeps a type awaiting vendor activation selectable, with the dry-run note', () => {
    const condition = classify().find((r) => r.resourceType === 'Condition')!;
    expect(condition.selectable).toBeTrue();
    expect(condition.note).toBe(AWAITING_ACTIVATION_NOTE);
    expect(classify({ activated: true }).find((r) => r.resourceType === 'Condition')?.note).toBeNull();
  });

  it('keeps the source\'s order and shows only the source\'s types', () => {
    expect(classify().map((r) => r.resourceType)).toEqual(['AllergyIntolerance', 'Condition', 'ServiceRequest', 'Encounter']);
  });

  it('adds a selected type outside the source\'s list as a greyed row, so it can be unticked', () => {
    const rows = classifyEhrWriteTypes({
      candidates: ['AllergyIntolerance'],
      capabilities,
      vendor: 'Healow',
      sourceIsTabular: false,
      vendorWriteApisActivated: true,
      selected: ['AllergyIntolerance', 'Immunization'],
      sourceDeclaresTypes: true,
    });
    expect(rows.map((r) => r.resourceType)).toEqual(['AllergyIntolerance', 'Immunization']);
    expect(rows[1]).toEqual({ resourceType: 'Immunization', selectable: false, reason: NOT_READ_BY_SOURCE_REASON, note: null });
  });

  it('a legacy source: a selected type the EHR no longer accepts gets a greyed row naming why', () => {
    const rows = classifyEhrWriteTypes({
      candidates: ['AllergyIntolerance'],
      capabilities,
      vendor: 'Epic',
      sourceIsTabular: false,
      vendorWriteApisActivated: true,
      selected: ['Encounter'],
      sourceDeclaresTypes: false,
    });
    expect(rows[1]).toEqual(
      jasmine.objectContaining({ resourceType: 'Encounter', selectable: false, reason: 'Not accepted by Epic' }),
    );
  });

  describe('Step 1 opt-ins', () => {
    const optInCaps = [
      capability('Procedure', { variant: 'external-radiotherapy-summary', requiresVariantOptIn: true }),
      capability('Condition', { variant: 'medical-history', createsHolderEncounter: true }),
      capability('Patient', { optInOnly: true }),
    ];
    const classifyWith = (optIns: {
      enabledVariants?: string[];
      createHolderEncounter?: boolean;
      createPatientIfMissing?: boolean;
    }) =>
      classifyEhrWriteTypes({
        candidates: ['Procedure', 'Condition', 'Patient'],
        capabilities: optInCaps,
        vendor: 'Epic',
        sourceIsTabular: false,
        vendorWriteApisActivated: true,
        optIns: {
          enabledVariants: optIns.enabledVariants ?? [],
          createHolderEncounter: optIns.createHolderEncounter ?? false,
          createPatientIfMissing: optIns.createPatientIfMissing ?? false,
        },
      });

    it('greys a type whose only API needs a variant or a holder encounter that is off, naming the opt-in', () => {
      const [procedure, condition] = classifyWith({});
      expect(procedure).toEqual(
        jasmine.objectContaining({ selectable: false, reason: 'Enable "External radiotherapy summaries" in Step 1' }),
      );
      expect(condition).toEqual(jasmine.objectContaining({ selectable: false, reason: HOLDER_ENCOUNTER_REASON }));
    });

    it('offers them once the opt-in is on', () => {
      const [procedure, condition] = classifyWith({
        enabledVariants: ['external-radiotherapy-summary'],
        createHolderEncounter: true,
      });
      expect(procedure.selectable).toBeTrue();
      expect(condition.selectable).toBeTrue();
    });

    it('notes that a Patient is created only through its opt-in', () => {
      expect(classifyWith({})[2]).toEqual(jasmine.objectContaining({ selectable: true, note: PATIENT_OPT_IN_NOTE }));
      expect(classifyWith({ createPatientIfMissing: true })[2].note).toBeNull();
    });
  });
});

describe('EhrWriteTypeGridComponent', () => {
  let fixture: ComponentFixture<EhrWriteTypeGridComponent>;
  let toggled: string[];

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [EhrWriteTypeGridComponent] });
    fixture = TestBed.createComponent(EhrWriteTypeGridComponent);
    toggled = [];
    fixture.componentInstance.toggled.subscribe((t) => toggled.push(t));
    fixture.componentRef.setInput('rows', [
      { resourceType: 'AllergyIntolerance', selectable: true, reason: null, note: null },
      { resourceType: 'Encounter', selectable: false, reason: 'Not accepted by Epic', note: null },
      { resourceType: 'Procedure', selectable: false, reason: 'Not accepted by Epic', note: null },
    ]);
    fixture.componentRef.setInput('selected', ['Procedure']);
    fixture.detectChanges();
  });

  const cards = () => Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('.ewtg-card'));

  it('shows greyed types with their reason and blocks ticking them', () => {
    const [allergy, encounter] = cards();
    expect(encounter.disabled).toBeTrue();
    expect(encounter.textContent).toContain('Not accepted by Epic');
    encounter.click();
    expect(toggled).toEqual([]);

    allergy.click();
    expect(toggled).toEqual(['AllergyIntolerance']);
  });

  it('lets an already-selected greyed type be unticked', () => {
    const procedure = cards()[2];
    expect(procedure.disabled).toBeFalse();
    procedure.click();
    expect(toggled).toEqual(['Procedure']);
  });

  it('filters by the wizard\'s search text', () => {
    fixture.componentRef.setInput('query', 'enc');
    fixture.detectChanges();
    expect(cards().map((c) => c.querySelector('.ewtg-name')?.textContent?.trim())).toEqual(['Encounter']);
  });
});
