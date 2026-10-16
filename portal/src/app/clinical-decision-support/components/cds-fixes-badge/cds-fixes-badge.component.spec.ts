import { TestBed } from '@angular/core/testing';
import { CdsFixesBadgeComponent } from './cds-fixes-badge.component';

describe('CdsFixesBadgeComponent', () => {
  function render(enabled: boolean): HTMLElement {
    TestBed.configureTestingModule({ imports: [CdsFixesBadgeComponent] });
    const fixture = TestBed.createComponent(CdsFixesBadgeComponent);
    fixture.componentRef.setInput('enabled', enabled);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows "Changes EHR drafts" when fixes are on', () => {
    expect(render(true).querySelector('[data-testid="cds-fixes-badge"]')?.textContent).toContain('Changes EHR drafts');
  });

  it('shows nothing when fixes are off', () => {
    expect(render(false).querySelector('[data-testid="cds-fixes-badge"]')).toBeNull();
  });
});
