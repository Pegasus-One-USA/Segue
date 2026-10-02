import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { FieldMappingListComponent } from './field-mapping-list.component';
import { FieldMappingAnchorService } from './field-mapping-anchor.service';
import { MappingRow } from './field-mapping-model';

/** isRowLocked is off by default, which is what the workflow builder relies on: Remove stays on every row.
 *  Only a host that marks a row locked (the Settings mapping-profile screen) gets the lock. */
describe('Field mapping list (v1) — row lock', () => {
  const row = (column: string, path: string): MappingRow => ({
    resource: 'Patient', tableName: 'patient', targetName: column, mode: 'value', instance: { type: 'first' },
    sources: [{ fhirPath: path, label: path, jsonPath: '$.' + path.split('.')[1], valueType: 'String', arrays: [] }],
  } as unknown as MappingRow);
  const rows = [row('PatientId', 'Patient.id'), row('Gender', 'Patient.gender')];

  function mount(isRowLocked?: (r: MappingRow) => boolean) {
    TestBed.configureTestingModule({
      imports: [FieldMappingListComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), FieldMappingAnchorService],
    });
    const fixture = TestBed.createComponent(FieldMappingListComponent);
    const set = (name: string, value: unknown) => fixture.componentRef.setInput(name, value);
    set('rows', rows);
    set('resources', ['Patient']);
    set('forest', []);
    set('tablesForResource', () => ['patient']);
    set('columnsFor', () => ['PatientId', 'Gender']);
    set('targetByResource', { Patient: 'patient' });
    set('isApproximated', () => false);
    if (isRowLocked) set('isRowLocked', isRowLocked);
    (fixture.componentInstance as unknown as { collapsed: { set(v: boolean): void } }).collapsed.set(false);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return Array.from(el.querySelectorAll<HTMLTableRowElement>('tbody tr')).filter(r => r.textContent!.includes('Patient.'));
  }

  it('by default (workflow builder) shows Remove on every row and no lock', () => {
    const bodyRows = mount();
    expect(bodyRows.length).toBe(2);
    for (const r of bodyRows) {
      expect(r.querySelector('.fm-list-link--danger')?.textContent?.trim()).toBe('Remove');
      expect(r.querySelector('.fm-list-lock')).toBeNull();
    }
  });

  it('shows the lock only on the row the host marks locked', () => {
    const [key, other] = mount(r => r.targetName === 'PatientId');
    expect(key.querySelector('.fm-list-lock')).not.toBeNull();
    expect(key.querySelector('.fm-list-link--danger')).toBeNull();
    expect(other.querySelector('.fm-list-link--danger')?.textContent?.trim()).toBe('Remove');
    expect(other.querySelector('.fm-list-lock')).toBeNull();
  });
});
