import { TestBed } from '@angular/core/testing';
import { TabularSourceFile } from '../../../../services/tabular-source.service';
import { TabularStreamCardComponent } from './tabular-stream-card.component';

/**
 * A saved CSV entry reopens with its file and row filter column chosen, even though the uploaded files (and so the
 * options) arrive after the entry: a select's own [value] is lost when its options render later.
 */
describe('TabularStreamCardComponent', () => {
  const file: TabularSourceFile = {
    id: 'f-1', fileName: 'mixed.csv', sizeBytes: 10, rowCount: 5, columns: ['record_type', 'patient_id'],
    createdBy: null, createdOnUtc: '2026-10-09T00:00:00Z',
  };

  it('shows the saved file and filter column once the files arrive', () => {
    TestBed.configureTestingModule({ imports: [TabularStreamCardComponent] });
    const fixture = TestBed.createComponent(TabularStreamCardComponent);
    fixture.componentRef.setInput('index', 0);
    fixture.componentRef.setInput('kind', 'csv');
    fixture.componentRef.setInput('entry', {
      resourceType: 'Patient', query: '', fileId: 'f-1', rowFilterColumn: 'record_type', rowFilterValue: 'patient', template: '{}',
    });
    fixture.componentRef.setInput('files', []);
    fixture.detectChanges();

    fixture.componentRef.setInput('files', [file]);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect((root.querySelector('#tab-f-0') as HTMLSelectElement).value).toBe('f-1');
    expect((root.querySelector('#tab-fc-0') as HTMLSelectElement).value).toBe('record_type');
  });
});
