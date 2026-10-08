import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { FieldMappingCanvasComponent } from './field-mapping-canvas.component';
import { ToastService } from '../../../../services/toast.service';
import { DestinationSchemaService } from '../../../../services/destination-schema.service';
import { TransformationRulesService } from './transformation-rules.service';
import { MappingRow } from './field-mapping-model';

/** A field ticked on the mapping list's "MPI Rule" tab is set on that one row, and unticking removes the key. */
describe('FieldMappingCanvasComponent — MPI Rule choice', () => {
  const identifier: MappingRow = {
    resource: 'Patient', sources: [{ fhirPath: 'Patient.identifier.value', label: 'Identifier' }], mode: 'value',
    instance: { type: 'first' }, targetName: 'Identifier', tableName: 'dbo.Patient',
  };
  const family: MappingRow = { ...identifier, sources: [{ fhirPath: 'Patient.name.family', label: 'Family' }], targetName: 'FamilyName' };

  async function canvasWith(rows: MappingRow[]) {
    await TestBed.configureTestingModule({
      imports: [FieldMappingCanvasComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ToastService, useValue: jasmine.createSpyObj<ToastService>('ToastService', ['show', 'success', 'error', 'warning']) },
        { provide: DestinationSchemaService, useValue: {} },
        { provide: TransformationRulesService, useValue: {
          getNodeSchemas: () => of([]), getEffectiveRules: () => of([]), list: () => of([]), delete: () => of(void 0),
        } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(FieldMappingCanvasComponent);
    fixture.componentRef.setInput('resources', ['Patient']);
    fixture.componentRef.setInput('destType', 'sql');
    fixture.componentRef.setInput('mappingRows', rows);
    fixture.componentRef.setInput('targetByResource', { Patient: 'dbo.Patient' });
    fixture.componentRef.setInput('availableFields', () => []);
    fixture.componentRef.setInput('columnsForResourceTarget', () => []);
    const emitted: MappingRow[][] = [];
    fixture.componentInstance.mappingRowsChange.subscribe(next => emitted.push(next));
    return { canvas: fixture.componentInstance, emitted };
  }

  it('sets the flag on the ticked row only', async () => {
    const { canvas, emitted } = await canvasWith([identifier, family]);

    canvas.onListMpiMatchChange({ resource: 'Patient', tableName: 'dbo.Patient', targetName: 'Identifier', isMpiMatch: true });

    expect(emitted[0].map(row => row.isMpiMatch)).toEqual([true, undefined]);
  });

  it('removes the key when unticked, leaving the row as it was before', async () => {
    const { canvas, emitted } = await canvasWith([{ ...identifier, isMpiMatch: true }]);

    canvas.onListMpiMatchChange({ resource: 'Patient', tableName: 'dbo.Patient', targetName: 'Identifier', isMpiMatch: false });

    expect(emitted[0][0]).toEqual(identifier);
  });
});
