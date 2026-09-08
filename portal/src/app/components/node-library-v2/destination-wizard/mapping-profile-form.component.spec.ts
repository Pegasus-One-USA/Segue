import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { MappingProfileFormComponent, MappingRow } from './mapping-profile-form.component';
import { MappingCatalogService } from '../../../services/mapping-catalog.service';

/**
 * Covers the two riskiest behaviors carried over by the docs/backend/14-mapping-profile-master-screen-plan.md
 * §5.1 extraction: the mandatory id-row reconciliation (_reconcileIdRows) that every mapping depends on, and
 * the §3.2 lossless round trip of the advanced MappingFieldDto members the wizard has no UI to author but must
 * not silently drop when a saved profile carries them.
 */
describe('MappingProfileFormComponent', () => {
  async function createComponent() {
    await TestBed.configureTestingModule({
      imports: [MappingProfileFormComponent],
      providers: [
        { provide: MappingCatalogService, useValue: { fields: () => of([]) } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(MappingProfileFormComponent);
    fixture.componentRef.setInput('resources', ['Patient']);
    fixture.componentRef.setInput('destType', 'csv');
    fixture.detectChanges();
    return fixture;
  }

  it('auto-inserts the mandatory id row as the upsert key once a resource is selected', async () => {
    const fixture = await createComponent();
    const rows = fixture.componentInstance.mappingRows();

    expect(rows.length).toBe(1);
    expect(rows[0].fhirPath).toBe('Patient.id');
    expect(rows[0].isUpsertKey).toBe(true);
  });

  it('never lets the mandatory id row be removed', async () => {
    const fixture = await createComponent();
    const idRow = fixture.componentInstance.mappingRows()[0];

    fixture.componentInstance.removeRow(idRow);

    expect(fixture.componentInstance.mappingRows().length).toBe(1);
  });

  it('round-trips every advanced MappingFieldDto-mirroring member through initialRows losslessly', async () => {
    const advancedRow: MappingRow = {
      resource: 'Patient',
      fieldLabel: 'Systolic BP',
      fhirPath: 'Patient.component.valueQuantity.value',
      targetName: 'Systolic',
      tableName: 'patients.csv',
      isUpsertKey: false,
      isRequired: true,
      defaultValue: '0',
      format: 'N2',
      normalizationType: 'Trim',
      terminologySystemJsonPath: '$.component[*].code.coding[*].system',
      terminologyCodeJsonPath: '$.component[*].code.coding[*].code',
      arrayPolicy: 'CorrelateByCode',
      cardinality: '0..*',
      correlationCodeJsonPath: '$.component[*].code.coding[*].code',
      correlationCodeValue: '8480-6',
      isEnabled: false,
    };

    await TestBed.configureTestingModule({
      imports: [MappingProfileFormComponent],
      providers: [
        { provide: MappingCatalogService, useValue: { fields: () => of([]) } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(MappingProfileFormComponent);
    fixture.componentRef.setInput('resources', ['Patient']);
    fixture.componentRef.setInput('destType', 'csv');
    fixture.componentRef.setInput('initialRows', [advancedRow]);
    fixture.detectChanges();

    const roundTripped = fixture.componentInstance.getRows().find(r => r.fieldLabel === 'Systolic BP');

    expect(roundTripped).toEqual(advancedRow);
  });

  it('addFields falls back to the built-in naming convention when no live schema is loaded', async () => {
    const fixture = await createComponent();

    fixture.componentInstance.addFields('Patient');

    const labels = fixture.componentInstance.mappingRows().map(r => r.fieldLabel);
    // The id field is excluded (already mandatory); every other built-in Patient field should be added.
    expect(labels).toContain('First Name');
    expect(labels).toContain('Last Name');
  });

  it('readOnly blocks mutation entirely', async () => {
    await TestBed.configureTestingModule({
      imports: [MappingProfileFormComponent],
      providers: [
        { provide: MappingCatalogService, useValue: { fields: () => of([]) } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(MappingProfileFormComponent);
    fixture.componentRef.setInput('resources', ['Patient']);
    fixture.componentRef.setInput('destType', 'csv');
    fixture.componentRef.setInput('readOnly', true);
    fixture.detectChanges();

    const before = fixture.componentInstance.mappingRows().length;
    fixture.componentInstance.addRow('Patient');

    expect(fixture.componentInstance.mappingRows().length).toBe(before);
  });
});
