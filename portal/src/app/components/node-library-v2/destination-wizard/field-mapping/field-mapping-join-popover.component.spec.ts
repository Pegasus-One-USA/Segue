import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { FieldMappingJoinPopoverComponent } from './field-mapping-join-popover.component';
import { MappingRow } from './field-mapping-model';
import { TransformationRulesService, TransformNodeSchema } from './transformation-rules.service';
import { ToastService } from '../../../../services/toast.service';

/**
 * A whole-node ("childJson") mapping — an array element dropped on a column as one JSON blob — used to be the
 * one row shape with no Transformation section at all, even though the engine has always supported
 * transforming it (ArrayListOperationsNode unwraps an ArrayPolicy.StoreJson value itself). These pin both
 * halves of making that section work there: it renders, and the rule is keyed by the node's own path, which
 * such a row carries as childNodeId rather than in `sources` (always empty for this shape).
 */
describe('FieldMappingJoinPopoverComponent — transformations on a whole-node (array) mapping', () => {
  const SCHEMAS: TransformNodeSchema[] = [
    { nodeType: 'ArrayListOperations', label: 'Array / list operations', fields: [] },
    { nodeType: 'StringNormalization', label: 'String normalization', fields: [] },
  ];

  let effectiveRuleFilters: Record<string, unknown>[];
  let savedRequests: Record<string, unknown>[];

  function createFixture(row: MappingRow, childArrayLabel: string | null = null) {
    effectiveRuleFilters = [];
    savedRequests = [];

    const rulesService = {
      getNodeSchemas: () => of(SCHEMAS),
      getEffectiveRules: (filter: Record<string, unknown>) => {
        effectiveRuleFilters.push(filter);
        return of([]);
      },
      save: (request: Record<string, unknown>) => {
        savedRequests.push(request);
        return of({ ...request, id: 'rule-1' });
      },
    };

    TestBed.configureTestingModule({
      imports: [FieldMappingJoinPopoverComponent],
      providers: [
        { provide: TransformationRulesService, useValue: rulesService },
        { provide: ToastService, useValue: { success: () => {}, error: () => {}, warning: () => {} } },
      ],
    });

    const fixture = TestBed.createComponent(FieldMappingJoinPopoverComponent);
    fixture.componentRef.setInput('row', row);
    fixture.componentRef.setInput('rulesDestinationType', 'sqlserver');
    fixture.componentRef.setInput('workflowId', 'wf-1');
    fixture.componentRef.setInput('childArrayLabel', childArrayLabel);
    fixture.detectChanges();
    return fixture;
  }

  function instancePicker(fixture: { nativeElement: HTMLElement }): HTMLSelectElement | null {
    return fixture.nativeElement.querySelector('#fm-inst-type');
  }

  const childJsonRow: MappingRow = {
    resource: 'Patient',
    sources: [],
    mode: 'childJson',
    childNodeId: 'Patient.name',
    targetName: 'NameConcat',
    tableName: 'dbo.Patient',
  };

  const valueRow: MappingRow = {
    resource: 'Patient',
    sources: [{ fhirPath: 'Patient.name.text', label: 'Text' }],
    mode: 'value',
    targetName: 'MultiplCols',
    tableName: 'dbo.Patient',
  };

  it('renders the Transformation section for a whole-node mapping', () => {
    const fixture = createFixture(childJsonRow);
    const toggle: HTMLElement | null = fixture.nativeElement.querySelector('.fm-popover-rule-toggle');

    expect(toggle).not.toBeNull();
    expect(toggle!.textContent).toContain('Transformation');
  });

  it('looks up the existing rule by the node path the row carries as childNodeId', () => {
    createFixture(childJsonRow);

    expect(effectiveRuleFilters.length).toBe(1);
    expect(effectiveRuleFilters[0]['sourceField']).toBe('Patient.name');
    expect(effectiveRuleFilters[0]['destinationField']).toBe('NameConcat');
  });

  it('saves the rule against that same node path', () => {
    const fixture = createFixture(childJsonRow);
    fixture.componentInstance.saveRule();

    expect(savedRequests.length).toBe(1);
    expect(savedRequests[0]['sourceField']).toBe('Patient.name');
  });

  // The value is an array exactly when the row still reads every repeat, and that is the only time a rule
  // should fan out over it. Handing a whole array to a scalar node instead is what parsed the JSON source
  // text itself as a value.
  it('runs the rule per item while the row reads the whole array', () => {
    const fixture = createFixture(childJsonRow);
    fixture.componentInstance.saveRule();

    expect(savedRequests[0]['arrayMode']).toBe('PerItem');
  });

  it('runs the rule once when the row has been narrowed to a single instance', () => {
    const fixture = createFixture({ ...childJsonRow, instance: { type: 'first' } });
    fixture.componentInstance.saveRule();

    expect(savedRequests[0]['arrayMode']).toBe('Whole');
  });

  it('runs an ordinary field mapping once, whatever its instance selection', () => {
    const fixture = createFixture(valueRow);
    fixture.componentInstance.saveRule();

    expect(savedRequests[0]['arrayMode']).toBe('Whole');
  });

  it('starts a whole-node mapping on the one node type that can unwrap its JSON', () => {
    const fixture = createFixture(childJsonRow);
    expect(fixture.componentInstance.ruleNodeType()).toBe('ArrayListOperations');
  });

  it('leaves an ordinary field mapping keyed by its own source path, on the usual default node type', () => {
    const fixture = createFixture(valueRow);

    expect(effectiveRuleFilters[0]['sourceField']).toBe('Patient.name.text');
    expect(fixture.componentInstance.ruleNodeType()).toBe('StringNormalization');
  });
});

/**
 * The instance picker used to live inside the popover's non-whole-node branch, so a whole-node mapping of a
 * repeating element -- the one row shape where "which repeat?" is the whole question -- had no way to ask
 * it. Whether such a row repeats is the host's answer (childArrayLabel), since the row itself carries no
 * sources to read array metadata from.
 */
describe('FieldMappingJoinPopoverComponent - instance selection on a whole-node (array) mapping', () => {
  function createFixture(row: MappingRow, childArrayLabel: string | null) {
    TestBed.configureTestingModule({
      imports: [FieldMappingJoinPopoverComponent],
      providers: [
        {
          provide: TransformationRulesService,
          useValue: { getNodeSchemas: () => of([]), getEffectiveRules: () => of([]), save: () => of({}) },
        },
        { provide: ToastService, useValue: { success: () => {}, error: () => {}, warning: () => {} } },
      ],
    });

    const fixture = TestBed.createComponent(FieldMappingJoinPopoverComponent);
    fixture.componentRef.setInput('row', row);
    fixture.componentRef.setInput('rulesDestinationType', 'sqlserver');
    fixture.componentRef.setInput('workflowId', 'wf-1');
    fixture.componentRef.setInput('childArrayLabel', childArrayLabel);
    fixture.detectChanges();
    return fixture;
  }

  const wholeNodeRow: MappingRow = {
    resource: 'Patient',
    sources: [],
    mode: 'childJson',
    childNodeId: 'Patient.name',
    targetName: 'NameConcat',
    tableName: 'dbo.Patient',
  };

  it('shows the picker, labelled with the repeating node the host resolved', () => {
    const fixture = createFixture(wholeNodeRow, 'Name');
    const label: HTMLElement | null = fixture.nativeElement.querySelector('label[for="fm-inst-type"]');

    expect(label).not.toBeNull();
    expect(label!.textContent).toContain('Name repeats');
  });

  it('hides the picker when the mapped node does not repeat at all', () => {
    const fixture = createFixture({ ...wholeNodeRow, childNodeId: 'Patient.maritalStatus' }, null);
    expect(fixture.nativeElement.querySelector('#fm-inst-type')).toBeNull();
  });

  it('defaults an unset whole-node selection to "all", which is what it has always done', () => {
    const fixture = createFixture(wholeNodeRow, 'Name');
    expect(fixture.componentInstance.instanceType()).toBe('all');
  });

  it('offers no delimited-string aggregation - "all records" here is already one JSON array', () => {
    const fixture = createFixture(wholeNodeRow, 'Name');
    expect(fixture.nativeElement.querySelector('input[type="checkbox"]')).toBeNull();
  });

  it('never stamps the scalar-only csv aggregate onto a whole-node row', () => {
    const fixture = createFixture({ ...wholeNodeRow, instance: { type: 'all' } }, 'Name');
    expect(fixture.componentInstance.draft()?.instance?.aggregate).toBeUndefined();
  });

  it('saves the picked instance on the row', () => {
    const fixture = createFixture(wholeNodeRow, 'Name');
    let saved: MappingRow | undefined;
    fixture.componentInstance.save.subscribe((r: MappingRow) => (saved = r));

    fixture.componentInstance.onInstanceTypeChange('nth');
    fixture.componentInstance.onInstanceField({ n: 2 });
    fixture.componentInstance.onSave();

    expect(saved?.instance).toEqual(jasmine.objectContaining({ type: 'nth', n: 2 }));
  });

  it('still forces the csv aggregate on an ordinary field mapping set to "all records"', () => {
    const valueRow: MappingRow = {
      resource: 'Patient',
      sources: [{ fhirPath: 'Patient.name.text', label: 'Text', arrays: ['Name'] }],
      mode: 'value',
      instance: { type: 'all' },
      targetName: 'MultiplCols',
      tableName: 'dbo.Patient',
    };
    const fixture = createFixture(valueRow, null);
    expect(fixture.componentInstance.draft()?.instance?.aggregate).toBe('csv');
    expect(fixture.nativeElement.querySelector('input[type="checkbox"]')).not.toBeNull();
  });
});

/**
 * The popover's two whole-node hints both used to state flatly that the ENTIRE node is stored. That is only
 * true while every repeat is read; once one instance is singled out the value is a single JSON object, and a
 * hint that still promised the whole array would contradict the picker directly above it.
 */
describe('FieldMappingJoinPopoverComponent - whole-node hints follow the instance selection', () => {
  function createFixture(row: MappingRow) {
    // Reset explicitly: the specs below compare two selections against each other, and TestBed refuses to be
    // reconfigured once a fixture has been created within the same spec.
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [FieldMappingJoinPopoverComponent],
      providers: [
        {
          provide: TransformationRulesService,
          useValue: { getNodeSchemas: () => of([]), getEffectiveRules: () => of([]), save: () => of({}) },
        },
        { provide: ToastService, useValue: { success: () => {}, error: () => {}, warning: () => {} } },
      ],
    });

    const fixture = TestBed.createComponent(FieldMappingJoinPopoverComponent);
    fixture.componentRef.setInput('row', row);
    fixture.componentRef.setInput('rulesDestinationType', 'sqlserver');
    fixture.componentRef.setInput('workflowId', 'wf-1');
    fixture.componentRef.setInput('childArrayLabel', 'Name');
    fixture.detectChanges();
    return fixture;
  }

  function wholeNodeRow(instance?: MappingRow['instance']): MappingRow {
    return {
      resource: 'Patient', sources: [], mode: 'childJson', childNodeId: 'Patient.name',
      instance, targetName: 'NameConcat', tableName: 'dbo.Patient',
    };
  }

  function hintText(fixture: { nativeElement: HTMLElement }): string {
    return fixture.nativeElement.querySelector('.fm-popover-hint')!.textContent!.replace(/\s+/g, ' ').trim();
  }

  it('still promises the entire node when every repeat is read', () => {
    expect(hintText(createFixture(wholeNodeRow()))).toContain('entire');
    expect(hintText(createFixture(wholeNodeRow({ type: 'all' })))).toContain('entire');
  });

  it('names the single instance once one is picked', () => {
    expect(hintText(createFixture(wholeNodeRow({ type: 'first' })))).toContain('the first instance');
    expect(hintText(createFixture(wholeNodeRow({ type: 'nth', n: 3 })))).toContain('instance #3');
  });

  it('describes "match criteria" as a narrowing, not as the whole node', () => {
    const fixture = createFixture(wholeNodeRow({ type: 'criteria', field: 'use', op: '=', value: 'official' }));
    expect(hintText(fixture)).toContain('the matching instances');
    expect(hintText(fixture)).not.toContain('entire');
    // The criteria is honoured by the engine now, so the approximation caveat is gone with it.
    expect(fixture.nativeElement.querySelector('.fm-popover-caveat')).toBeNull();
  });
});
