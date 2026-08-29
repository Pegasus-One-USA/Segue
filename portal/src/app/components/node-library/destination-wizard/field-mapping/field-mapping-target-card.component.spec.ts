import { TestBed } from '@angular/core/testing';
import { FieldMappingTargetCardComponent } from './field-mapping-target-card.component';
import { FieldMappingAnchorService } from './field-mapping-anchor.service';

/**
 * Regression coverage for the "+Add column" button staying disabled ("Select a real destination table
 * above first") after a MySQL Map Fields save -> leave -> reopen round trip: the live schema probe
 * qualifies MySQL table names with the connected database's name (e.g. "fhirbridge_output.Patient", see
 * SqlDestinationSchemaService.ReadColumnsAsync), while a saved/reopened mapping's targetValue only ever
 * holds the bare table name (bareName(), field-mapping-summary.model.ts). hasValidTarget() must tolerate
 * that bare-vs-qualified mismatch for MySQL specifically, without loosening SQL Server/PostgreSQL's strict
 * fullName match (their dbo./public. qualification genuinely matches what the live probe returns).
 */
describe('FieldMappingTargetCardComponent — MySQL bare-name hasValidTarget fix', () => {
  async function createComponent() {
    await TestBed.configureTestingModule({
      imports: [FieldMappingTargetCardComponent],
      providers: [FieldMappingAnchorService],
    }).compileComponents();

    const fixture = TestBed.createComponent(FieldMappingTargetCardComponent);
    fixture.componentRef.setInput('resource', 'Patient');
    fixture.componentRef.setInput('tableName', 'Patient');
    fixture.componentRef.setInput('columns', []);
    fixture.componentRef.setInput('rowForColumn', () => undefined);
    fixture.componentRef.setInput('isArmed', false);
    fixture.componentRef.setInput('isApproximated', () => false);
    fixture.componentRef.setInput('x', 0);
    fixture.componentRef.setInput('y', 0);
    return fixture;
  }

  it('MySQL: enables "+ Add column" when targetValue is the bare name restored from a saved mapping', async () => {
    const fixture = await createComponent();
    fixture.componentRef.setInput('destType', 'mysql');
    fixture.componentRef.setInput('targetValue', 'Patient'); // bare, as bareName() always restores for MySQL
    fixture.componentRef.setInput('hasSqlTables', true);
    fixture.componentRef.setInput('sqlTableOptions', ['fhirbridge_output.Patient']); // live-probed, qualified
    fixture.componentRef.setInput('sqlTableNames', ['Patient']);
    fixture.detectChanges();

    expect(fixture.componentInstance.hasValidTarget()).toBe(true);
  });

  it('MySQL: still disables "+ Add column" when targetValue matches neither the qualified nor the bare list', async () => {
    const fixture = await createComponent();
    fixture.componentRef.setInput('destType', 'mysql');
    fixture.componentRef.setInput('targetValue', 'SomeOtherTable');
    fixture.componentRef.setInput('hasSqlTables', true);
    fixture.componentRef.setInput('sqlTableOptions', ['fhirbridge_output.Patient']);
    fixture.componentRef.setInput('sqlTableNames', ['Patient']);
    fixture.detectChanges();

    expect(fixture.componentInstance.hasValidTarget()).toBe(false);
  });

  it('SQL Server: a bare-name-only match is NOT tolerated — strict fullName matching is unchanged', async () => {
    const fixture = await createComponent();
    fixture.componentRef.setInput('destType', 'sql');
    fixture.componentRef.setInput('targetValue', 'Patient'); // bare
    fixture.componentRef.setInput('hasSqlTables', true);
    fixture.componentRef.setInput('sqlTableOptions', ['dbo.Patient']);
    fixture.componentRef.setInput('sqlTableNames', ['Patient']); // present but must be ignored for non-MySQL
    fixture.detectChanges();

    expect(fixture.componentInstance.hasValidTarget()).toBe(false);
  });

  it('PostgreSQL: a bare-name-only match is NOT tolerated — strict fullName matching is unchanged', async () => {
    const fixture = await createComponent();
    fixture.componentRef.setInput('destType', 'postgres');
    fixture.componentRef.setInput('targetValue', 'Patient');
    fixture.componentRef.setInput('hasSqlTables', true);
    fixture.componentRef.setInput('sqlTableOptions', ['public.Patient']);
    fixture.componentRef.setInput('sqlTableNames', ['Patient']);
    fixture.detectChanges();

    expect(fixture.componentInstance.hasValidTarget()).toBe(false);
  });
});
