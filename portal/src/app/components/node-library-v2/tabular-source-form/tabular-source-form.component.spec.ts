import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { TabularCheckResult, TabularSourceService } from '../../../services/tabular-source.service';
import { TabularSourceFormComponent } from './tabular-source-form.component';

/**
 * The CSV / SQL Table source names its resource types, each with its own query or file and template. An older node
 * (one query through every template) opens as one entry per template reading that same query, and nothing saves
 * until a check of exactly the current settings has passed.
 */
describe('TabularSourceFormComponent', () => {
  const passed: TabularCheckResult = {
    allPassed: true,
    streams: [{ index: 0, resourceType: 'AllergyIntolerance', passed: true, problems: [], columns: ['allergy_id'], missingColumns: [], rowsRead: null, resources: [], rowErrors: [] }],
  };

  let api: jasmine.SpyObj<TabularSourceService>;

  function create(fields: Record<string, string>): TabularSourceFormComponent {
    api = jasmine.createSpyObj<TabularSourceService>('TabularSourceService', [
      'templatePresets', 'listSqlConnections', 'check', 'getFile', 'testSqlConnection', 'upload',
    ]);
    api.templatePresets.and.returnValue(of([]));
    api.listSqlConnections.and.returnValue(of([]));
    api.check.and.returnValue(of(passed));
    TestBed.configureTestingModule({
      imports: [TabularSourceFormComponent],
      providers: [{ provide: TabularSourceService, useValue: api }],
    });
    const fixture = TestBed.createComponent(TabularSourceFormComponent);
    fixture.componentRef.setInput('initialFields', fields);
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  const legacy = {
    __name: 'Allergies',
    tab_kind: 'sql',
    tab_datasetKey: 'clinic-allergies',
    tab_sqlEngine: 'postgresql',
    tab_secretKeyVaultName: 'kv',
    tab_secretName: 'tabular-sql-postgresql-1',
    tab_query: 'SELECT * FROM allergies',
    tab_templates: JSON.stringify([{ resourceType: 'AllergyIntolerance', template: { resourceType: 'AllergyIntolerance', id: '{{allergy_id}}' } }]),
  };

  it('opens an older single-query node as one entry per template reading that same query', () => {
    const form = create(legacy);

    expect(form.entries().map(e => [e.resourceType, e.query])).toEqual([['AllergyIntolerance', 'SELECT * FROM allergies']]);
  });

  it('does not save until a check of the current settings has passed, then saves each type on its own', () => {
    const form = create(legacy);

    expect(form.getFields()).toBeNull();

    form.check();
    const fields = form.getFields()!;

    expect(api.check).toHaveBeenCalledWith(jasmine.objectContaining({ kind: 'sql', secretName: 'tabular-sql-postgresql-1', preview: false }));
    expect(JSON.parse(fields['tab_streams'])).toEqual([jasmine.objectContaining({ resourceType: 'AllergyIntolerance', query: 'SELECT * FROM allergies' })]);
    expect(fields['tab_templates']).toBe('');
    expect(fields['Resources']).toBe('AllergyIntolerance');
  });

  it('asks for a new check when anything changes after one passed', () => {
    const form = create(legacy);
    form.check();

    form.updateEntry(0, { query: 'SELECT * FROM allergies_v2' });

    expect(form.isChecked()).toBeFalse();
    expect(form.getFields()).toBeNull();
  });
});
