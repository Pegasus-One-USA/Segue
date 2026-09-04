import { TestBed } from '@angular/core/testing';
import { FieldMappingCanvasComponent } from './field-mapping-canvas.component';
import { ToastService } from '../../../../services/toast.service';
import { DestinationSchemaService } from '../../../../services/destination-schema.service';

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
        { provide: ToastService, useValue: { show: () => {} } },
        { provide: DestinationSchemaService, useValue: {} },
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
