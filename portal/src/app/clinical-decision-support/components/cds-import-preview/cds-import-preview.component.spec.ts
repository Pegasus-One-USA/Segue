import { TestBed } from '@angular/core/testing';
import { CdsImportPreviewComponent } from './cds-import-preview.component';
import { CdsCoverageImportResult } from '../../models/cds-coverage.models';
import { makeImportResult } from '../../testing/cds-test-data';

describe('CdsImportPreviewComponent', () => {
  function create(result: CdsCoverageImportResult) {
    TestBed.configureTestingModule({ imports: [CdsImportPreviewComponent] });
    const fixture = TestBed.createComponent(CdsImportPreviewComponent);
    fixture.componentRef.setInput('result', result);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const rows = () => Array.from(el.querySelectorAll('[data-testid="cds-import-row"]')).map(r => r.textContent ?? '');
    return { fixture, el, rows };
  }

  it('shows the counts and every row with its action', () => {
    const { el, rows } = create(makeImportResult());
    expect(el.querySelector('[data-testid="cds-import-added"]')?.textContent).toContain('1');
    expect(el.querySelector('[data-testid="cds-import-updated"]')?.textContent).toContain('1');
    expect(el.querySelector('[data-testid="cds-import-deleted"]')).toBeNull();
    expect(rows().length).toBe(2);
    expect(rows()[0]).toContain('Add');
    expect(rows()[0]).toContain('70551');
    expect(rows()[0]).toContain('Always');
    expect(rows()[1]).toContain('Update');
  });

  it('shows row errors, the delete count for a replace, and can show errors only', () => {
    const { el, fixture, rows } = create(makeImportResult({
      strategy: 'replace', deleted: 1, errorCount: 1,
      rows: [
        { rowNumber: 2, payer: 'Acme Health Plan', system: 'http://www.ama-assn.org/go/cpt', code: '70551', effectiveFrom: '2026-01-01', action: 'add', errors: [] },
        { rowNumber: 3, payer: 'Nobody', system: null, code: null, effectiveFrom: null, action: 'error', errors: ["No payer is named 'Nobody'. Add the payer first."] },
        { rowNumber: 0, payer: 'Acme Health Plan', system: 'http://www.ama-assn.org/go/cpt', code: '99999', effectiveFrom: '2000-01-01', action: 'delete', errors: [] },
      ],
    }));
    expect(el.querySelector('[data-testid="cds-import-deleted"]')?.textContent).toContain('1');
    expect(rows()[1]).toContain('No payer is named');
    expect(rows()[2]).toContain('In Segue but not in the file');
    expect(rows()[2]).toContain('—');

    const filter = el.querySelector('[data-testid="cds-import-filter"]') as HTMLSelectElement;
    filter.value = 'error';
    filter.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(rows().length).toBe(1);
    expect(rows()[0]).toContain('Error');
  });

  it('shows problems with the file as a whole', () => {
    const { el } = create(makeImportResult({
      totalRows: 0, added: 0, updated: 0, errorCount: 1, rows: [], fileErrors: ['The file has no "code" column.'],
    }));
    expect(el.querySelector('[data-testid="cds-import-file-errors"]')?.textContent).toContain('no "code" column');
    expect(el.querySelector('[data-testid="cds-import-filter"]')).toBeNull();
  });
});
