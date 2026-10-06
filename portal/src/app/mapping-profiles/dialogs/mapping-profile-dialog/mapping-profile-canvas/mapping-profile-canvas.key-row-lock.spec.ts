import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { MappingProfileCanvasComponent } from './mapping-profile-canvas.component';
import { FieldMappingListComponent } from '../../../../components/node-library/destination-wizard/field-mapping/field-mapping-list.component';
import { MappingCatalogService } from '../../../../services/mapping-catalog.service';
import { ToastService } from '../../../../services/toast.service';
import { DestinationSchemaService } from '../../../../services/destination-schema.service';
import { TransformationRulesService } from '../../../../components/node-library/destination-wizard/field-mapping/transformation-rules.service';

/** The Settings mapping-profile screen always keeps the resource's id mapped as the upsert key (reconcileIdRow),
 *  so its Remove link only ever put the row straight back. It shows a lock there instead. */
describe('Mapping profile canvas — locked id/upsert-key row', () => {
  async function mount() {
    await TestBed.configureTestingModule({
      imports: [MappingProfileCanvasComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MappingCatalogService, useValue: { fields: () => of([]) } },
        { provide: ToastService, useValue: { show: jasmine.createSpy('show') } },
        { provide: DestinationSchemaService, useValue: {} },
        { provide: TransformationRulesService, useValue: {
            getNodeSchemas: () => of([]), getEffectiveRules: () => of([]), delete: () => of(void 0),
          } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(MappingProfileCanvasComponent);
    fixture.componentRef.setInput('resources', ['Patient']);
    fixture.componentRef.setInput('destType', 'csv');
    fixture.detectChanges();
    await fixture.whenStable();
    const list = fixture.debugElement.query(By.directive(FieldMappingListComponent)).componentInstance as FieldMappingListComponent;
    (list as unknown as { collapsed: { set(v: boolean): void } }).collapsed.set(false);
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    const keyRow = () => Array.from(el.querySelectorAll<HTMLTableRowElement>('tbody tr'))
      .find(r => r.textContent!.includes('Patient.id'));
    return { fixture, list, keyRow };
  }

  it('shows a lock instead of Remove on the key row, keeping Edit', async () => {
    const { keyRow } = await mount();
    const row = keyRow()!;
    expect(row).toBeTruthy();
    expect(row.querySelector('.fm-list-lock')?.getAttribute('title')).toBe("Upsert key — always mapped, so it can't be removed");
    expect(row.querySelector('.fm-list-link--danger')).toBeNull();
    expect(row.textContent).toContain('Edit…');
  });

  async function openEditFor(fixture: Awaited<ReturnType<typeof mount>>['fixture'], row: HTMLTableRowElement) {
    Array.from(row.querySelectorAll<HTMLButtonElement>('button.fm-list-link')).find(b => b.textContent!.trim() === 'Edit…')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return document.querySelector<HTMLElement>('.fm-popover-footer')!;
  }

  it("Edit on the key row: the popover shows the lock instead of Delete mapping, and Save is still there", async () => {
    const { fixture, keyRow } = await mount();
    const footer = await openEditFor(fixture, keyRow()!);

    expect(footer.querySelector('.fm-popover-locked')?.textContent?.trim()).toBe('🔒 Upsert key — always mapped');
    expect(footer.querySelector('.fm-popover-delete')).toBeNull();
    expect(footer.textContent).toContain('Save');
  });

  it('Edit on any other row still offers Delete mapping, and it removes the row', async () => {
    const { fixture } = await mount();
    const canvas = fixture.componentInstance;
    const key = canvas.rowsRich().find(r => r.sources[0]?.fhirPath === 'Patient.id')!;
    canvas.rowsRich.set([...canvas.rowsRich(), {
      ...key, isUpsertKey: false, targetName: 'Gender',
      sources: [{ ...key.sources[0], fhirPath: 'Patient.gender', label: 'Gender', jsonPath: '$.gender' }],
    }]);
    fixture.detectChanges();
    const genderRow = () => Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<HTMLTableRowElement>('tbody tr'))
      .find(r => r.textContent!.includes('Patient.gender'));

    const footer = await openEditFor(fixture, genderRow()!);
    expect(footer.querySelector('.fm-popover-locked')).toBeNull();
    footer.querySelector<HTMLButtonElement>('.fm-popover-delete')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(genderRow()).toBeUndefined();
  });

  it('moves the key row to the new main table when the main table changes', async () => {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [MappingProfileCanvasComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MappingCatalogService, useValue: { fields: () => of([]) } },
        { provide: ToastService, useValue: { show: jasmine.createSpy('show') } },
        { provide: DestinationSchemaService, useValue: {} },
        { provide: TransformationRulesService, useValue: {
            getNodeSchemas: () => of([]), getEffectiveRules: () => of([]), delete: () => of(void 0),
          } },
      ],
    }).compileComponents();
    const column = (name: string, pk: boolean) => ({ name, dataType: 'nvarchar', mappingValueType: 'String', isNullable: !pk, maxLength: 200, isPrimaryKey: pk, isUnique: pk, isAutoGenerated: false });
    const table = (name: string) => ({ schemaName: 'dbo', tableName: name, fullName: 'dbo.' + name, columns: [column('PatientId', true)] });

    const fixture = TestBed.createComponent(MappingProfileCanvasComponent);
    fixture.componentRef.setInput('resources', ['Patient']);
    fixture.componentRef.setInput('destType', 'sql');
    fixture.componentRef.setInput('sqlTables', [table('A')]);
    // Editing a profile whose saved table no longer exists in the destination.
    fixture.componentRef.setInput('initialTargets', { Patient: 'dbo.Gone' });
    fixture.detectChanges();
    await fixture.whenStable();
    const keyTable = () => fixture.componentInstance.rowsRich().find(r => r.sources[0]?.fhirPath === 'Patient.id')?.tableName;
    expect(keyTable()).toBe('dbo.Gone');

    fixture.componentInstance.onTargetByResourceChange({ Patient: 'dbo.A' });   // what the canvas's "+ Add a table" emits
    fixture.detectChanges();
    await fixture.whenStable();

    expect(keyTable()).toBe('dbo.A');
  });

  describe('key column on a table with no primary-key or unique column', () => {
    const column = (name: string, pk = false) => ({ name, dataType: 'nvarchar', mappingValueType: 'String', isNullable: !pk, maxLength: 200, isPrimaryKey: pk, isUnique: pk, isAutoGenerated: false });

    async function keyColumnFor(columns: unknown[], before?: (c: MappingProfileCanvasComponent) => void) {
      TestBed.resetTestingModule();
      await TestBed.configureTestingModule({
        imports: [MappingProfileCanvasComponent],
        providers: [
          provideHttpClient(),
          provideHttpClientTesting(),
          { provide: MappingCatalogService, useValue: { fields: () => of([]) } },
          { provide: ToastService, useValue: { show: jasmine.createSpy('show') } },
          { provide: DestinationSchemaService, useValue: {} },
          { provide: TransformationRulesService, useValue: {
              getNodeSchemas: () => of([]), getEffectiveRules: () => of([]), delete: () => of(void 0),
            } },
        ],
      }).compileComponents();
      const fixture = TestBed.createComponent(MappingProfileCanvasComponent);
      fixture.componentRef.setInput('resources', ['Patient']);
      fixture.componentRef.setInput('destType', 'sql');
      fixture.componentRef.setInput('sqlTables', [{ schemaName: 'dbo', tableName: 'T', fullName: 'dbo.T', columns }]);
      fixture.detectChanges();
      await fixture.whenStable();
      before?.(fixture.componentInstance);
      fixture.componentInstance.onTargetByResourceChange({ Patient: 'dbo.T' });
      fixture.detectChanges();
      await fixture.whenStable();
      return { fixture, key: () => fixture.componentInstance.rowsRich().find(r => r.sources[0]?.fhirPath === 'Patient.id')?.targetName };
    }

    it('a primary-key column still wins over a name match', async () => {
      const { key } = await keyColumnFor([column('Id'), column('MyKey', true), column('PatientId')]);
      expect(key()).toBe('MyKey');
    });

    it('finds <Resource>Id by name, case-insensitively', async () => {
      const { key } = await keyColumnFor([column('Identifier'), column('PATIENTID'), column('Id')]);
      expect(key()).toBe('PATIENTID');
    });

    it('then Id', async () => {
      const { key } = await keyColumnFor([column('Identifier'), column('id')]);
      expect(key()).toBe('id');
    });

    it('with no match, keeps the built-in default as before', async () => {
      const { key } = await keyColumnFor([column('Identifier'), column('FamilyName')]);
      expect(key()).toBe('SourcePatientId');
    });

    it('keeps a column on the table that is already set, rather than re-matching', async () => {
      const { fixture, key } = await keyColumnFor([column('Identifier'), column('PatientId')]);
      const canvas = fixture.componentInstance;
      canvas.rowsRich.set(canvas.rowsRich().map(r => (r.sources[0]?.fhirPath === 'Patient.id' ? { ...r, targetName: 'Identifier' } : r)));
      fixture.detectChanges();
      await fixture.whenStable();
      expect(key()).toBe('Identifier');
    });
  });

  it('ignores a remove request for the key row even if one is made directly', async () => {
    const { fixture, list, keyRow } = await mount();
    const emitted = jasmine.createSpy('removeRow');
    list.removeRow.subscribe(emitted);
    const row = fixture.componentInstance.rowsRich().find(r => r.sources[0]?.fhirPath === 'Patient.id')!;

    list.onRemove(row);
    fixture.detectChanges();

    expect(emitted).not.toHaveBeenCalled();
    expect(keyRow()).toBeTruthy();
  });
});
