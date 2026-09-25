import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { FieldMappingCanvasComponent } from './field-mapping-canvas.component';
import { ToastService } from '../../../../services/toast.service';
import { DestinationSchemaService } from '../../../../services/destination-schema.service';
import { TransformationRulesService } from './transformation-rules.service';
import { of, throwError } from 'rxjs';

/**
 * Regression coverage for the "Map Fields table card disappears after Save -> leave -> reopen" bug
 * (MySQL only): the live schema probe qualifies MySQL table names with the connected database's name
 * (e.g. "fhirbridge_output.FamilyMemberHistory", see SqlDestinationSchemaService.ReadColumnsAsync), while
 * a saved/reopened mapping's targetByResource only ever holds the bare table name (bareName(),
 * field-mapping-summary.model.ts). isPrimaryTargetValid() must tolerate that bare-vs-qualified mismatch
 * for MySQL specifically, without loosening SQL Server/PostgreSQL's strict fullName match (their dbo./
 * public. qualification genuinely matches what the live probe returns).
 */
describe('FieldMappingCanvasComponent — MySQL bare-name reopen fix', () => {
  async function createComponent() {
    await TestBed.configureTestingModule({
      imports: [FieldMappingCanvasComponent],
      providers: [
        // The canvas renders field-mapping-list, which injects DeIdentificationProfileService -> HttpClient.
        // Without these the whole suite failed to construct the component at all (10/10 NullInjectorError).
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ToastService, useValue: { show: () => {} } },
        { provide: DestinationSchemaService, useValue: {} },
        { provide: TransformationRulesService, useValue: {
            // The join popover calls getNodeSchemas() on construction; the rest is what removeRow's own
            // rule cleanup needs.
            getNodeSchemas: () => of([]), getEffectiveRules: () => of([]), delete: () => of(void 0),
          } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(FieldMappingCanvasComponent);
    return fixture;
  }

  it('MySQL: renders the primary table card after reopen, when targetByResource only holds the bare name a saved mapping restores', async () => {
    const fixture = await createComponent();
    fixture.componentRef.setInput('resources', ['FamilyMemberHistory']);
    fixture.componentRef.setInput('destType', 'mysql');
    fixture.componentRef.setInput('mappingRows', []);
    // Reopen state: targetByResource restored bare (as applyMappingSummaryDocument's bareName() always
    // does for MySQL) — the exact shape a save -> leave -> reopen round trip produces.
    fixture.componentRef.setInput('targetByResource', { FamilyMemberHistory: 'FamilyMemberHistory' });
    fixture.componentRef.setInput('availableFields', () => []);
    fixture.componentRef.setInput('columnsForResourceTarget', () => []);
    fixture.componentRef.setInput('hasSqlTables', true);
    // Live schema probe result: MySQL qualifies with the connected database name.
    fixture.componentRef.setInput('sqlTableOptions', ['fhirbridge_output.FamilyMemberHistory']);
    fixture.componentRef.setInput('sqlTableNames', ['FamilyMemberHistory']);
    fixture.detectChanges();

    const cards = fixture.componentInstance.targetCards();
    expect(cards).toEqual([
      { resource: 'FamilyMemberHistory', tableName: 'FamilyMemberHistory', isExtra: false },
    ]);
  });

  it('MySQL: still hides the primary card when the target matches neither the qualified nor the bare live-schema list', async () => {
    const fixture = await createComponent();
    fixture.componentRef.setInput('resources', ['FamilyMemberHistory']);
    fixture.componentRef.setInput('destType', 'mysql');
    fixture.componentRef.setInput('mappingRows', []);
    fixture.componentRef.setInput('targetByResource', { FamilyMemberHistory: 'SomeOtherTable' });
    fixture.componentRef.setInput('availableFields', () => []);
    fixture.componentRef.setInput('columnsForResourceTarget', () => []);
    fixture.componentRef.setInput('hasSqlTables', true);
    fixture.componentRef.setInput('sqlTableOptions', ['fhirbridge_output.FamilyMemberHistory']);
    fixture.componentRef.setInput('sqlTableNames', ['FamilyMemberHistory']);
    fixture.detectChanges();

    expect(fixture.componentInstance.targetCards()).toEqual([]);
  });

  it('SQL Server: a bare-name-only match is NOT tolerated — strict fullName matching is unchanged', async () => {
    const fixture = await createComponent();
    fixture.componentRef.setInput('resources', ['Patient']);
    fixture.componentRef.setInput('destType', 'sql');
    fixture.componentRef.setInput('mappingRows', []);
    // Same shape of mismatch as the MySQL case above (bare "Patient" vs qualified "dbo.Patient") —
    // for SQL Server this must NOT be tolerated, since dbo. qualification genuinely matches the live probe.
    fixture.componentRef.setInput('targetByResource', { Patient: 'Patient' });
    fixture.componentRef.setInput('availableFields', () => []);
    fixture.componentRef.setInput('columnsForResourceTarget', () => []);
    fixture.componentRef.setInput('hasSqlTables', true);
    fixture.componentRef.setInput('sqlTableOptions', ['dbo.Patient']);
    fixture.componentRef.setInput('sqlTableNames', ['Patient']); // present but must be ignored for non-MySQL
    fixture.detectChanges();

    expect(fixture.componentInstance.targetCards()).toEqual([]);
  });

  it('PostgreSQL: a bare-name-only match is NOT tolerated — strict fullName matching is unchanged', async () => {
    const fixture = await createComponent();
    fixture.componentRef.setInput('resources', ['Patient']);
    fixture.componentRef.setInput('destType', 'postgres');
    fixture.componentRef.setInput('mappingRows', []);
    fixture.componentRef.setInput('targetByResource', { Patient: 'Patient' });
    fixture.componentRef.setInput('availableFields', () => []);
    fixture.componentRef.setInput('columnsForResourceTarget', () => []);
    fixture.componentRef.setInput('hasSqlTables', true);
    fixture.componentRef.setInput('sqlTableOptions', ['public.Patient']);
    fixture.componentRef.setInput('sqlTableNames', ['Patient']);
    fixture.detectChanges();

    expect(fixture.componentInstance.targetCards()).toEqual([]);
  });

  it('MySQL: extra/child table cards are restored and rendered regardless of qualification (no validity gate on extras)', async () => {
    const fixture = await createComponent();
    fixture.componentRef.setInput('resources', ['FamilyMemberHistory']);
    fixture.componentRef.setInput('destType', 'mysql');
    fixture.componentRef.setInput('mappingRows', []);
    fixture.componentRef.setInput('targetByResource', { FamilyMemberHistory: 'FamilyMemberHistory' });
    fixture.componentRef.setInput('availableFields', () => []);
    fixture.componentRef.setInput('columnsForResourceTarget', () => []);
    fixture.componentRef.setInput('hasSqlTables', true);
    fixture.componentRef.setInput('sqlTableOptions', ['fhirbridge_output.FamilyMemberHistory']);
    fixture.componentRef.setInput('sqlTableNames', ['FamilyMemberHistory']);
    // A restored extra/child table, saved bare (same round trip as the primary target).
    fixture.componentRef.setInput('extraTables', ['FamilyMemberHistory_Contact']);
    fixture.detectChanges();

    const cards = fixture.componentInstance.targetCards();
    expect(cards).toEqual([
      { resource: 'FamilyMemberHistory', tableName: 'FamilyMemberHistory', isExtra: false },
      { resource: 'FamilyMemberHistory', tableName: 'FamilyMemberHistory_Contact', isExtra: true },
    ]);
  });
});


/**
 * Removing a mapping must take its transformation rule with it. Rules are keyed server-side by
 * (resource, destination field, source field) rather than by the mapping row, so a row deleted on its own
 * left the rule resolvable — and a mapping later recreated onto the same column silently picked it back up.
 */
describe('FieldMappingCanvasComponent — removing a mapping deletes its transformation rule', () => {
  const ROW = {
    resource: 'Observation',
    sources: [{ fhirPath: 'Observation.valueQuantity.value', label: 'value' }],
    mode: 'value' as const,
    targetName: 'TransformationQuantityRange',
    tableName: 'public.Observation',
  };

  async function createComponent(rulesService: unknown) {
    await TestBed.configureTestingModule({
      imports: [FieldMappingCanvasComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ToastService, useValue: { show: () => {}, success: () => {}, error: () => {} } },
        { provide: DestinationSchemaService, useValue: {} },
        { provide: TransformationRulesService, useValue: { getNodeSchemas: () => of([]), ...(rulesService as object) } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(FieldMappingCanvasComponent);
    fixture.componentRef.setInput('resources', ['Observation']);
    fixture.componentRef.setInput('mappingRows', [ROW]);
    fixture.componentRef.setInput('rulesDestinationType', 'PostgreSql');
    fixture.componentRef.setInput('workflowId', 'wf-1');
    fixture.componentRef.setInput('availableFields', () => []);
    fixture.componentRef.setInput('columnsForResourceTarget', () => []);
    return fixture;
  }

  it('deletes the rule the removed row owned', async () => {
    const deleted: string[] = [];
    const fixture = await createComponent({
      getEffectiveRules: () => of([{ id: 'rule-1' }] as never),
      delete: (id: string) => { deleted.push(id); return of(void 0); },
    });

    fixture.componentInstance.removeRow('Observation', 'public.Observation', 'TransformationQuantityRange');

    expect(deleted).toEqual(['rule-1']);
  });

  it('looks the rule up scoped to this workflow only, so a shared tenant-wide rule is never deleted', async () => {
    let query: Record<string, unknown> | undefined;
    const fixture = await createComponent({
      getEffectiveRules: (q: Record<string, unknown>) => { query = q; return of([] as never); },
      delete: () => of(void 0),
    });

    fixture.componentInstance.removeRow('Observation', 'public.Observation', 'TransformationQuantityRange');

    expect(query?.['workflowScopedOnly']).toBeTrue();
    expect(query?.['destinationField']).toBe('TransformationQuantityRange');
    expect(query?.['sourceField']).toBe('Observation.valueQuantity.value');
    // A rule authored before the workflow's first save is stored unattached — without this the rule just
    // created on a never-yet-saved pipeline would survive its own mapping being deleted.
    expect(query?.['includePending']).toBeTrue();
  });

  it('still removes the row when the rule lookup fails', async () => {
    const emitted: unknown[] = [];
    const fixture = await createComponent({
      getEffectiveRules: () => throwError(() => new Error('offline')),
      delete: () => of(void 0),
    });
    fixture.componentInstance.mappingRowsChange.subscribe((rows: unknown) => emitted.push(rows));

    expect(() =>
      fixture.componentInstance.removeRow('Observation', 'public.Observation', 'TransformationQuantityRange'),
    ).not.toThrow();
    expect(emitted).toEqual([[]]);
  });

  it('does nothing rule-side when the row was not there to begin with', async () => {
    let called = false;
    const fixture = await createComponent({
      getEffectiveRules: () => { called = true; return of([] as never); },
      delete: () => of(void 0),
    });

    fixture.componentInstance.removeRow('Observation', 'public.Observation', 'NoSuchColumn');

    expect(called).toBeFalse();
  });
});
