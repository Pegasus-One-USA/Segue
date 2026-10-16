import { TestBed } from '@angular/core/testing';
import { CDS_COVERAGE_INFORMATION_URL, CdsSystemActionsComponent, cdsCoverageDecisionsOf } from './cds-system-actions.component';
import { CdsAction } from '../../models/cds.models';

describe('CdsSystemActionsComponent', () => {
  const coverageInfo = (covered: string, pa?: string) => ({
    url: CDS_COVERAGE_INFORMATION_URL,
    extension: [
      { url: 'coverage', valueReference: { reference: 'Coverage/c1' } },
      { url: 'covered', valueCode: covered },
      ...(pa ? [{ url: 'pa-needed', valueCode: pa }] : []),
      { url: 'coverage-assertion-id', valueString: 'a1' },
    ],
  });

  function render(actions: CdsAction[]) {
    TestBed.configureTestingModule({ imports: [CdsSystemActionsComponent] });
    const fixture = TestBed.createComponent(CdsSystemActionsComponent);
    fixture.componentRef.setInput('actions', actions);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('reads the coverage information on an order and ignores other extensions', () => {
    expect(cdsCoverageDecisionsOf({
      resourceType: 'ServiceRequest',
      extension: [{ url: 'http://example.org/other' }, coverageInfo('covered', 'auth-needed')],
    })).toEqual(['Covered, prior authorization needed']);
    expect(cdsCoverageDecisionsOf(undefined)).toEqual([]);
  });

  it('lists each order update with its target and decision', () => {
    const el = render([{
      type: 'update',
      description: 'Coverage information from Example Health Plan',
      resource: { resourceType: 'ServiceRequest', id: 'draft-mri-brain', extension: [coverageInfo('not-covered')] },
    }]);
    expect(el.textContent).toContain('Order updates (1)');
    const item = el.querySelector('[data-testid="cds-system-action"]')!;
    expect(item.textContent).toContain('Coverage information from Example Health Plan');
    expect(item.textContent).toContain('ServiceRequest/draft-mri-brain');
    expect(item.textContent).toContain('Not covered');
  });

  it('shows nothing when there are no updates', () => {
    expect(render([]).querySelector('[data-testid="cds-system-actions"]')).toBeNull();
  });
});
