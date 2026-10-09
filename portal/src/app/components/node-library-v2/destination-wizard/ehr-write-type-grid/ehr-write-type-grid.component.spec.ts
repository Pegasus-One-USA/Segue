import { ComponentFixture, TestBed } from '@angular/core/testing';
import { EhrWriteCapability } from '../../../../services/ehr-write-capabilities.service';
import { EhrWriteTypeGridComponent } from './ehr-write-type-grid.component';
import {
  AWAITING_ACTIVATION_NOTE,
  EhrWriteTypeRow,
  NEEDS_TABULAR_SOURCE_REASON,
  NOT_READ_BY_SOURCE_REASON,
  PATIENT_OPT_IN_NOTE,
  classifyEhrWriteTypes,
} from './ehr-write-type-grid.model';
import {
  ALWAYS_INCLUDED_NOTE,
  EhrWriteKindSwitches,
  HOLDER_ENCOUNTER_KIND_NOTE,
  NO_KIND_CHOSEN_NOTE,
  NO_KIND_SWITCHES,
  ehrReviewTypeLines,
  kindsFor,
  setKind,
  sharedSwitchNote,
  switchesFor,
  typesWithoutKind,
  withTypeTicked,
} from './ehr-write-kinds.model';
import {
  ATHENA_CAPABILITIES,
  ECW_CAPABILITIES,
  EPIC_CAPABILITIES,
  GENERIC_FHIR_CAPABILITIES,
} from './ehr-write-capabilities.fixtures';
import { EhrWriteKindToggle } from './ehr-write-type-grid.component';

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
    expect(rows[1]).toEqual({ resourceType: 'Immunization', selectable: false, reason: NOT_READ_BY_SOURCE_REASON, note: null, kinds: [] });
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

  describe('kinds under a type', () => {
    const classifyFor = (vendor: string, caps: EhrWriteCapability[], sourceIsTabular = false, createPatientIfMissing = false) =>
      classifyEhrWriteTypes({
        candidates: [...new Set(caps.map((c) => c.resourceType))],
        capabilities: caps,
        vendor,
        sourceIsTabular,
        vendorWriteApisActivated: true,
        optIns: { enabledVariants: [], createHolderEncounter: false, createPatientIfMissing },
      });
    const kindsOf = (rows: EhrWriteTypeRow[], type: string) =>
      rows.find((r) => r.resourceType === type)!.kinds.map((k) => `${k.label}${k.optional ? '' : ' (always)'}`);

    it('Epic, from an EHR source: plain kinds per type, the CSV / SQL-only ones left out', () => {
      const rows = classifyFor('Epic', EPIC_CAPABILITIES);
      expect(kindsOf(rows, 'Observation')).toEqual(['Vital signs (always)', 'Lines, drains and airways']);
      expect(kindsOf(rows, 'DocumentReference')).toEqual(['Clinical notes (always)', 'Scanned documents']);
      expect(kindsOf(rows, 'BodyStructure')).toEqual(['Radiotherapy volumes']);
      expect(kindsOf(rows, 'Procedure')).toEqual(['External radiotherapy summaries']);
      expect(kindsOf(rows, 'ServiceRequest')).toEqual(['External radiotherapy summaries']);
      // Written one way only, always: nothing to choose.
      expect(kindsOf(rows, 'Condition')).toEqual([]);
      expect(kindsOf(rows, 'AllergyIntolerance')).toEqual([]);
    });

    it('Epic, from a CSV / SQL source: the kinds needing the EHR\'s own ids are shown too', () => {
      const rows = classifyFor('Epic', EPIC_CAPABILITIES, true);
      expect(kindsOf(rows, 'Observation')).toEqual(['Vital signs (always)', 'Lines, drains and airways', 'CT radiation dose']);
      expect(kindsOf(rows, 'DocumentReference'))
        .toEqual(['Clinical notes (always)', 'Scanned documents', 'Non-patient documents']);
      expect(kindsOf(rows, 'Communication')).toEqual(['Community resource referral messages']);
      expect(kindsOf(rows, 'QuestionnaireResponse')).toEqual(['Patient-entered questionnaire answers']);
    });

    it('a type whose only kinds need a CSV / SQL source stays greyed with the reason for an EHR source', () => {
      const rows = classifyFor('Epic', EPIC_CAPABILITIES);
      for (const type of ['Communication', 'QuestionnaireResponse']) {
        expect(rows.find((r) => r.resourceType === type)).toEqual(
          { resourceType: type, selectable: false, reason: NEEDS_TABULAR_SOURCE_REASON, note: null, kinds: [] });
      }
      expect(classifyFor('Epic', EPIC_CAPABILITIES, true).find((r) => r.resourceType === 'Communication')?.selectable).toBeTrue();
    });

    it('eClinicalWorks: Condition and Procedure kinds, history on a holder encounter', () => {
      const rows = classifyFor('Healow', ECW_CAPABILITIES);
      expect(kindsOf(rows, 'Condition')).toEqual(['Problem list (always)', 'Encounter diagnosis (always)', 'Medical history']);
      expect(kindsOf(rows, 'Procedure')).toEqual(['Surgical history']);
      expect(rows.find((r) => r.resourceType === 'Condition')!.kinds[2])
        .toEqual(jasmine.objectContaining({ id: 'medical-history', variant: null, holderEncounter: true, optional: true }));
      expect(kindsOf(rows, 'MedicationRequest')).toEqual([]);
    });

    it('athenahealth: Observation is vital signs and lab results, both always included', () => {
      const rows = classifyFor('Athenahealth', ATHENA_CAPABILITIES);
      expect(kindsOf(rows, 'Observation')).toEqual(['Vital signs (always)', 'Lab results (always)']);
      expect(rows.filter((r) => r.resourceType !== 'Observation').every((r) => r.kinds.length === 0)).toBeTrue();
    });

    it('a FHIR server shows no kinds at all', () => {
      expect(classifyFor('GenericFhir', GENERIC_FHIR_CAPABILITIES).every((r) => r.kinds.length === 0)).toBeTrue();
    });

    it('names only a Patient as created through "Create the patient", never another type written only on opt-in', () => {
      const rows = classifyFor('Epic', EPIC_CAPABILITIES);
      expect(rows.find((r) => r.resourceType === 'Patient')!.note).toBe(PATIENT_OPT_IN_NOTE);
      expect(rows.find((r) => r.resourceType === 'BodyStructure')!.note).toBeNull();
      expect(rows.find((r) => r.resourceType === 'Procedure')!.note).toBeNull();
      expect(classifyFor('Healow', ECW_CAPABILITIES).find((r) => r.resourceType === 'Procedure')!.note).toBeNull();
      expect(classifyFor('Epic', EPIC_CAPABILITIES, false, true).find((r) => r.resourceType === 'Patient')!.note).toBeNull();
    });

    describe('switches', () => {
      const epicKind = (type: string, id: string) => kindsFor(type, EPIC_CAPABILITIES, true).find((k) => k.id === id)!;

      it('an optional kind is its saved switch; an always-included one has none', () => {
        let s: EhrWriteKindSwitches = NO_KIND_SWITCHES;
        s = setKind(s, epicKind('Observation', 'lines-drains-airways'), true);
        s = setKind(s, epicKind('Observation', 'vital-signs'), false);
        expect(s).toEqual({ enabledVariants: ['lines-drains-airways'], createHolderEncounter: false });
        expect(setKind(s, epicKind('Observation', 'lines-drains-airways'), false).enabledVariants).toEqual([]);

        const history = kindsFor('Procedure', ECW_CAPABILITIES, false)[0];
        expect(setKind(NO_KIND_SWITCHES, history, true)).toEqual({ enabledVariants: [], createHolderEncounter: true });
      });

      it('ticking a type written only through optional kinds turns them on; one with an always-included kind is left as is', () => {
        expect(withTypeTicked('BodyStructure', EPIC_CAPABILITIES, false, NO_KIND_SWITCHES).enabledVariants)
          .toEqual(['radiotherapy-volume']);
        expect(withTypeTicked('Observation', EPIC_CAPABILITIES, false, NO_KIND_SWITCHES)).toBe(NO_KIND_SWITCHES);
        expect(withTypeTicked('Procedure', ECW_CAPABILITIES, false, NO_KIND_SWITCHES).createHolderEncounter).toBeTrue();
      });

      it('saves only the switches a selected type uses from this source', () => {
        const all = {
          enabledVariants: ['lines-drains-airways', 'radiotherapy-volume', 'dicom-image-characteristics'],
          createHolderEncounter: true,
        };
        expect(switchesFor(['Observation'], EPIC_CAPABILITIES, false, all))
          .toEqual({ enabledVariants: ['lines-drains-airways'], createHolderEncounter: false });
        expect(switchesFor(['Observation'], EPIC_CAPABILITIES, true, all).enabledVariants)
          .toEqual(['lines-drains-airways', 'dicom-image-characteristics']);
        expect(switchesFor(['Condition'], ECW_CAPABILITIES, false, all).createHolderEncounter).toBeTrue();
      });

      it('finds a selected type none of whose kinds is on', () => {
        expect(typesWithoutKind(['Procedure', 'Observation', 'Condition'], EPIC_CAPABILITIES, false, NO_KIND_SWITCHES))
          .toEqual(['Procedure']);
      });

      it('says what a shared switch does to the other ticked types, for the next click', () => {
        const rows = classifyFor('Healow', ECW_CAPABILITIES);
        const medical = rows.find((r) => r.resourceType === 'Condition')!.kinds[2];
        const surgical = rows.find((r) => r.resourceType === 'Procedure')!.kinds[0];
        const off = NO_KIND_SWITCHES;
        const on = { enabledVariants: [], createHolderEncounter: true };
        expect(sharedSwitchNote(medical, rows, ['Condition'], on)).toBe('');
        const problems = rows.find((r) => r.resourceType === 'Condition')!.kinds[0];
        expect(sharedSwitchNote(problems, rows, ['Condition', 'Procedure'], on)).toBe('');

        // Off: ticking it ticks the other type's kind too.
        expect(sharedSwitchNote(medical, rows, ['Condition', 'Procedure'], off)).toBe('Also ticks Surgical history under Procedure');
        expect(sharedSwitchNote(surgical, rows, ['Condition'], off)).toBe('Also ticks Medical history under Condition');
        // On, Condition keeps other kinds: unticking Medical history unticks Procedure, whose only kind it shared.
        expect(sharedSwitchNote(medical, rows, ['Condition', 'Procedure'], on))
          .toBe('Unticking also unticks Surgical history under Procedure, and so Procedure (its only kind)');
        // On, Surgical history is Procedure's only kind: unticking it unticks Procedure alone, so it only names the link.
        expect(sharedSwitchNote(surgical, rows, ['Condition', 'Procedure'], on)).toBe('Same setting as Medical history under Condition');
        // On through the other type while this one is not ticked: why its box shows ticked once it is.
        expect(sharedSwitchNote(surgical, rows, ['Condition'], on)).toBe('Same setting as Medical history under Condition');
      });

      it('Epic: the Procedure and ServiceRequest summaries are one setting, never "Also ticks" on a ticked box', () => {
        const rows = classifyFor('Epic', EPIC_CAPABILITIES);
        const summary = rows.find((r) => r.resourceType === 'Procedure')!.kinds
          .find((k) => k.id === 'external-radiotherapy-summary')!;
        const both = ['Procedure', 'ServiceRequest'];
        expect(sharedSwitchNote(summary, rows, both, NO_KIND_SWITCHES))
          .toBe('Also ticks External radiotherapy summaries under ServiceRequest');
        expect(sharedSwitchNote(summary, rows, both, { enabledVariants: ['external-radiotherapy-summary'], createHolderEncounter: false }))
          .toBe('Same setting as External radiotherapy summaries under ServiceRequest');
      });

      it('lists each selected type with the kinds written, for Review', () => {
        expect(ehrReviewTypeLines(['AllergyIntolerance', 'Observation', 'DocumentReference'], EPIC_CAPABILITIES, false,
          { enabledVariants: ['lines-drains-airways'], createHolderEncounter: false })).toEqual([
          { resourceType: 'AllergyIntolerance', kinds: [] },
          { resourceType: 'Observation', kinds: ['Vital signs', 'Lines, drains and airways'] },
          { resourceType: 'DocumentReference', kinds: ['Clinical notes'] },
        ]);
      });
    });
  });
});

describe('EhrWriteTypeGridComponent', () => {
  let fixture: ComponentFixture<EhrWriteTypeGridComponent>;
  let toggled: string[];
  let kindToggled: EhrWriteKindToggle[];

  const observationKinds = kindsFor('Observation', EPIC_CAPABILITIES, false);

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [EhrWriteTypeGridComponent] });
    fixture = TestBed.createComponent(EhrWriteTypeGridComponent);
    toggled = [];
    kindToggled = [];
    fixture.componentInstance.toggled.subscribe((t) => toggled.push(t));
    fixture.componentInstance.kindToggled.subscribe((t) => kindToggled.push(t));
    fixture.componentRef.setInput('rows', [
      { resourceType: 'AllergyIntolerance', selectable: true, reason: null, note: null, kinds: [] },
      { resourceType: 'Encounter', selectable: false, reason: 'Not accepted by Epic', note: null, kinds: [] },
      { resourceType: 'Procedure', selectable: false, reason: 'Not accepted by Epic', note: null, kinds: [] },
      { resourceType: 'Observation', selectable: true, reason: null, note: null, kinds: observationKinds },
    ]);
    fixture.componentRef.setInput('selected', ['Procedure']);
    fixture.detectChanges();
  });

  const el = () => fixture.nativeElement as HTMLElement;
  const cards = () => Array.from(el().querySelectorAll<HTMLButtonElement>('.ewtg-toggle'));
  const kindBox = (id: string) => el().querySelector<HTMLInputElement>(`[data-kind="${id}"] input`)!;

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

  it('lists a type\'s kinds under it, unticked while the type is not ticked, and says what they are', () => {
    expect(el().querySelectorAll('[data-type="Observation"] [data-kind]').length).toBe(2);
    expect(el().querySelectorAll('[data-type="AllergyIntolerance"] [data-kind]').length).toBe(0);
    expect(kindBox('vital-signs').checked).toBeFalse();
    expect(kindBox('vital-signs').disabled).toBeFalse();
    expect(el().textContent).toContain('tick the kinds of record to include');

    kindBox('lines-drains-airways').click();
    expect(kindToggled).toEqual([{ resourceType: 'Observation', kindId: 'lines-drains-airways' }]);
    expect(toggled).toEqual([]);
  });

  it('a ticked type: its always-included kind is ticked and locked, an optional one follows its switch', () => {
    fixture.componentRef.setInput('selected', ['Observation']);
    fixture.detectChanges();
    expect(kindBox('vital-signs').checked).toBeTrue();
    expect(kindBox('vital-signs').disabled).toBeTrue();
    expect(el().querySelector('[data-kind="vital-signs"]')!.textContent).toContain(ALWAYS_INCLUDED_NOTE);
    expect(kindBox('lines-drains-airways').checked).toBeFalse();

    fixture.componentRef.setInput('switches', { enabledVariants: ['lines-drains-airways'], createHolderEncounter: false });
    fixture.detectChanges();
    expect(kindBox('lines-drains-airways').checked).toBeTrue();
    expect(kindBox('lines-drains-airways').disabled).toBeFalse();
  });

  it('a ticked eClinicalWorks history kind says it is filed on a new telephone encounter, and what else it ticks', () => {
    const rows = classifyEhrWriteTypes({
      candidates: ['Condition', 'Procedure'], capabilities: ECW_CAPABILITIES, vendor: 'Healow', sourceIsTabular: false,
      vendorWriteApisActivated: true,
    });
    fixture.componentRef.setInput('rows', rows);
    fixture.componentRef.setInput('selected', ['Condition', 'Procedure']);
    fixture.componentRef.setInput('switches', { enabledVariants: [], createHolderEncounter: true });
    fixture.detectChanges();

    const condition = el().querySelector('[data-type="Condition"]')!;
    expect(condition.querySelector('[data-testid="ewkl-holder-note"]')!.textContent).toContain(HOLDER_ENCOUNTER_KIND_NOTE);
    expect(condition.querySelector('[data-testid="ewkl-shared-note"]')!.textContent)
      .toContain('Unticking also unticks Surgical history under Procedure, and so Procedure (its only kind)');
    expect(el().querySelector('[data-type="Procedure"] [data-testid="ewkl-shared-note"]')!.textContent)
      .toContain('Same setting as Medical history under Condition');

    fixture.componentRef.setInput('switches', NO_KIND_SWITCHES);
    fixture.detectChanges();
    expect(condition.querySelector('[data-testid="ewkl-holder-note"]')).toBeNull();
    // Procedure is left ticked with no kind (a destination saved before kinds): its card says what to do.
    expect(el().querySelector('[data-type="Procedure"] [data-testid="ewkl-none-chosen"]')!.textContent)
      .toContain(NO_KIND_CHOSEN_NOTE);
    expect(condition.querySelector('[data-testid="ewkl-shared-note"]')!.textContent)
      .toContain('Also ticks Surgical history under Procedure');
  });
});
