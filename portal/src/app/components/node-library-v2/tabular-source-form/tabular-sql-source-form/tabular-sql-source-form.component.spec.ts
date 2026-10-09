import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { TabularCheckResult, TabularSourceService, TabularSqlConnection } from '../../../../services/tabular-source.service';
import { CanvasNode } from '../../../../models/node-v2.model';
import { SOURCE_FORM_REGISTRY } from '../../source-form.registry';
import { sourceFormKeyForNode } from '../../source-node-vendor.util';
import { TabularSqlSourceFormComponent } from './tabular-sql-source-form.component';

/**
 * The "SQL database" source names its resource types, each with its own query and template. An older node (one query
 * through every template) opens as one entry per template reading that same query, and nothing saves until a check
 * of exactly the current settings has passed. The name and the data set key fill themselves in from the database.
 */
describe('TabularSqlSourceFormComponent', () => {
  const passed: TabularCheckResult = {
    allPassed: true,
    streams: [{ index: 0, resourceType: 'AllergyIntolerance', passed: true, problems: [], columns: ['allergy_id'], missingColumns: [], rowsRead: null, resources: [], rowErrors: [] }],
  };
  const clinicDb: TabularSqlConnection = {
    id: '7d1c', name: 'Clinic DB', engine: 'postgresql', secretKeyVaultName: 'kv', secretName: 'tabular-sql-postgresql-1', createdBy: null, updatedOnUtc: null,
  };

  let api: jasmine.SpyObj<TabularSourceService>;

  function create(fields: Record<string, string> | null): TabularSqlSourceFormComponent {
    api = jasmine.createSpyObj<TabularSourceService>('TabularSourceService', [
      'templatePresets', 'listSqlConnections', 'check', 'getFile', 'testSqlConnection', 'upload',
    ]);
    api.templatePresets.and.returnValue(of([]));
    api.listSqlConnections.and.returnValue(of([clinicDb]));
    api.check.and.returnValue(of(passed));
    TestBed.configureTestingModule({
      imports: [TabularSqlSourceFormComponent],
      providers: [{ provide: TabularSourceService, useValue: api }],
    });
    const fixture = TestBed.createComponent(TabularSqlSourceFormComponent);
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
    expect(fields['tab_kind']).toBe('sql');
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

  it('keeps a saved name and a saved data set key exactly as they are', () => {
    const form = create(legacy);

    form.onConnection(clinicDb);

    expect(form.form.controls.name.value).toBe('Allergies');
    expect(form.form.controls.datasetKey.value).toBe('clinic-allergies');
  });

  it('a new source is named after its database and takes its data set key from it', () => {
    const form = create(null);

    form.onConnection(clinicDb);

    expect(form.form.controls.name.value).toBe('SQL: Clinic DB');
    expect(form.form.controls.datasetKey.value).toBe('sql-clinic-db-7d1c');
  });

  it('stops filling the name in once the user types one, and saves the automatic one when left empty', () => {
    const form = create(null);
    form.form.controls.name.setValue('My import');
    form.onNameInput();

    form.onConnection(clinicDb);
    expect(form.form.controls.name.value).toBe('My import');

    form.form.controls.name.setValue('');
    form.onNameInput();
    expect(form.form.controls.name.value).toBe('SQL: Clinic DB');
  });

  it('opens Advanced when the data set identity cannot be used', () => {
    const form = create(legacy);
    form.form.controls.datasetKey.setValue('--');
    form.check();

    expect(form.getFields()).toBeNull();
    expect(form.advancedOpen()).toBeTrue();
  });

  it('a saved node reopens as the tile its tab_kind names', () => {
    const node = (fields: Record<string, string>) => ({ id: 'n1', fields, connectorLabel: 'CSV / SQL Table' } as unknown as CanvasNode);

    expect(sourceFormKeyForNode(node({ tab_kind: 'sql', Connector: 'CSV / SQL Table' }))).toBe('tabular-sql');
    expect(sourceFormKeyForNode(node({ tab_kind: 'csv', Connector: 'CSV / SQL Table' }))).toBe('tabular-csv');
    expect(SOURCE_FORM_REGISTRY['tabular-sql']).toBe(TabularSqlSourceFormComponent);
  });
});
