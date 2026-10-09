import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { TabularCheckResult, TabularSourceFile, TabularSourceService } from '../../../../services/tabular-source.service';
import { normalizeDatasetKey } from '../tabular-dataset-key';
import { TabularCsvSourceFormComponent } from './tabular-csv-source-form.component';

/**
 * The "CSV file" source: the name fills in from the first file, and the data set key is made once when the node is
 * created and kept from then on, through a corrected file and a reopen.
 */
describe('TabularCsvSourceFormComponent', () => {
  const passed: TabularCheckResult = {
    allPassed: true,
    streams: [{ index: 0, resourceType: 'Patient', passed: true, problems: [], columns: ['id'], missingColumns: [], rowsRead: null, resources: [], rowErrors: [] }],
  };
  const file = (id: string, fileName: string): TabularSourceFile => ({
    id, fileName, sizeBytes: 10, rowCount: 1, columns: ['id'], createdBy: null, createdOnUtc: '2026-10-09T00:00:00Z',
  });

  let api: jasmine.SpyObj<TabularSourceService>;

  function create(fields: Record<string, string> | null): TabularCsvSourceFormComponent {
    api = jasmine.createSpyObj<TabularSourceService>('TabularSourceService', ['templatePresets', 'check', 'getFile', 'upload']);
    api.templatePresets.and.returnValue(of([]));
    api.check.and.returnValue(of(passed));
    api.getFile.and.callFake((id: string) => of(file(id, 'saved.csv')));
    TestBed.configureTestingModule({
      imports: [TabularCsvSourceFormComponent],
      providers: [{ provide: TabularSourceService, useValue: api }],
    });
    const fixture = TestBed.createComponent(TabularCsvSourceFormComponent);
    fixture.componentRef.setInput('initialFields', fields);
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  it('a new source gets a data set key of its own that a corrected file does not change', () => {
    const form = create(null);
    const key = form.form.controls.datasetKey.value;

    expect(normalizeDatasetKey(key)).toBe(key);

    form.onFileUploaded(file('f1', 'patients.csv'));
    form.toggleType('Patient', true);
    form.onFileUploaded(file('f2', 'patients-fixed.csv'));
    form.check();
    const fields = form.getFields()!;

    expect(fields['tab_kind']).toBe('csv');
    expect(fields['tab_datasetKey']).toBe(key);
    expect(fields['__name']).toBe('CSV: patients.csv');
  });

  it('keeps the saved key and name when an existing node reopens', () => {
    const form = create({
      __name: 'Patients',
      tab_kind: 'csv',
      tab_datasetKey: 'Clinic Patients',
      tab_streams: JSON.stringify([{ resourceType: 'Patient', fileId: 'f1', template: { resourceType: 'Patient', id: '{{id}}' } }]),
    });

    expect(form.form.controls.datasetKey.value).toBe('Clinic Patients');
    expect(form.form.controls.name.value).toBe('Patients');
  });

  it('a node saved under the old default name takes the automatic one', () => {
    const form = create({
      __name: 'CSV / SQL Table',
      tab_kind: 'csv',
      tab_datasetKey: 'clinic-patients',
      tab_streams: JSON.stringify([{ resourceType: 'Patient', fileId: 'f1', template: { resourceType: 'Patient', id: '{{id}}' } }]),
    });

    expect(form.form.controls.name.value).toBe('CSV: saved.csv');
  });
});
