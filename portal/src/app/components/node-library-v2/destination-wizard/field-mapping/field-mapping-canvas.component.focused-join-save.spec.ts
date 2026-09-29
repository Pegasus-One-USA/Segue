import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { FieldMappingCanvasComponent } from './field-mapping-canvas.component';
import { MappingRow } from './field-mapping-model';
import { ToastService } from '../../../../services/toast.service';
import { DestinationSchemaService } from '../../../../services/destination-schema.service';
import { TransformationRulesService } from './transformation-rules.service';

/**
 * Saving the popover while it is focused on ONE source of a join splices that source back into the real row
 * rather than writing the narrowed draft over it. Only the fields that view can actually edit may come back
 * with it — `jsonWriteMode` is not one of them.
 *
 * supportsJsonWriteMode() is false for a join (the backend joins the pieces into a delimited string, never a
 * JSON document), but the narrowed draft has exactly ONE source, so the check passes there and the popover
 * offers the choice. Carrying it back wrote a setting onto the join that serializeRowsFlat then ignored: the
 * UI implied an effect it never had, and the stray value reappeared on every reopen of that connector line.
 */
describe('FieldMappingCanvasComponent — saving a join focused on one source', () => {
  const JOIN_ROW: MappingRow = {
    resource: 'Patient',
    sources: [
      { fhirPath: 'Patient.name.given', label: 'Given', valueType: 'Json' },
      { fhirPath: 'Patient.name.family', label: 'Family', valueType: 'String' },
    ],
    mode: 'value',
    delimiter: ' ',
    targetName: 'FullName',
    tableName: 'patients',
  };

  async function createComponent(rows: MappingRow[]) {
    await TestBed.configureTestingModule({
      imports: [FieldMappingCanvasComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ToastService,
          useValue: jasmine.createSpyObj('ToastService', ['show', 'success', 'warning', 'error']),
        },
        { provide: DestinationSchemaService, useValue: {} },
        {
          provide: TransformationRulesService,
          useValue: { getNodeSchemas: () => of([]), getEffectiveRules: () => of([]), delete: () => of(void 0) },
        },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(FieldMappingCanvasComponent);
    fixture.componentRef.setInput('resources', ['Patient']);
    fixture.componentRef.setInput('destType', 'mongo');
    fixture.componentRef.setInput('mappingRows', rows);
    fixture.componentRef.setInput('targetByResource', { Patient: 'patients' });
    fixture.componentRef.setInput('availableFields', () => []);
    fixture.componentRef.setInput('columnsForResourceTarget', () => []);
    fixture.componentRef.setInput('hasSqlTables', true);
    fixture.componentRef.setInput('sqlTableOptions', ['patients']);
    fixture.componentRef.setInput('sqlTableNames', ['patients']);
    fixture.detectChanges();
    return fixture;
  }

  /** The draft the popover emits for a connector-line click on one source of the join. */
  function focusedDraft(overrides: Partial<MappingRow> = {}): MappingRow {
    return { ...JOIN_ROW, sources: [JOIN_ROW.sources[0]], delimiter: undefined, ...overrides };
  }

  it('does not carry jsonWriteMode from the narrowed draft onto the join', async () => {
    const fixture = await createComponent([JOIN_ROW]);
    let saved: MappingRow[] | undefined;
    fixture.componentInstance.mappingRowsChange.subscribe(rows => (saved = rows));

    fixture.componentInstance.popoverKey.set({
      resource: 'Patient', tableName: 'patients', targetName: 'FullName',
      focusSourceFhirPath: 'Patient.name.given',
    });
    fixture.componentInstance.onPopoverSave(focusedDraft({ jsonWriteMode: 'document' }));

    expect(saved![0].jsonWriteMode).toBeUndefined();
  });

  it('still carries the fields that view can edit, and keeps every other source', async () => {
    const fixture = await createComponent([JOIN_ROW]);
    let saved: MappingRow[] | undefined;
    fixture.componentInstance.mappingRowsChange.subscribe(rows => (saved = rows));

    fixture.componentInstance.popoverKey.set({
      resource: 'Patient', tableName: 'patients', targetName: 'FullName',
      focusSourceFhirPath: 'Patient.name.given',
    });
    fixture.componentInstance.onPopoverSave(
      focusedDraft({ instance: { type: 'first' }, referencesResource: 'Practitioner' }));

    expect(saved![0].sources.map(s => s.fhirPath))
      .toEqual(['Patient.name.given', 'Patient.name.family'], 'the hidden source survives the save');
    expect(saved![0].instance).toEqual({ type: 'first' });
    expect(saved![0].referencesResource).toBe('Practitioner');
    expect(saved![0].delimiter).toBe(' ', 'a field the narrowed view never showed comes from the real row');
  });

  /** An ordinary (non-focused) save is untouched — a single-source row may legitimately set the mode. */
  it('leaves jsonWriteMode alone when the save is not a focused-join save', async () => {
    const singleRow: MappingRow = {
      resource: 'Patient',
      sources: [{ fhirPath: 'Patient.name', label: 'Name', valueType: 'Json' }],
      mode: 'value', targetName: 'Name', tableName: 'patients',
    };
    const fixture = await createComponent([singleRow]);
    let saved: MappingRow[] | undefined;
    fixture.componentInstance.mappingRowsChange.subscribe(rows => (saved = rows));

    fixture.componentInstance.popoverKey.set({
      resource: 'Patient', tableName: 'patients', targetName: 'Name', focusSourceFhirPath: null,
    });
    fixture.componentInstance.onPopoverSave({ ...singleRow, jsonWriteMode: 'document' });

    expect(saved![0].jsonWriteMode).toBe('document');
  });
});
