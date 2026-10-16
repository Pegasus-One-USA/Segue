import { TestBed } from '@angular/core/testing';
import { CdsOverrideReasonsEditorComponent } from './cds-override-reasons-editor.component';
import {
  CDS_DEFAULT_OVERRIDE_REASONS,
  CDS_OVERRIDE_REASON_MAX,
  CdsReasonsArray,
  buildCdsReasonsArray,
  makeCdsReasonGroup,
} from '../../models/cds-service-options.util';

describe('CdsOverrideReasonsEditorComponent', () => {
  function render(reasons: CdsReasonsArray = buildCdsReasonsArray(null)) {
    TestBed.configureTestingModule({ imports: [CdsOverrideReasonsEditorComponent] });
    const fixture = TestBed.createComponent(CdsOverrideReasonsEditorComponent);
    fixture.componentRef.setInput('reasons', reasons);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const click = (sel: string) => {
      (el.querySelector(sel) as HTMLButtonElement).click();
      fixture.detectChanges();
    };
    const rows = () => el.querySelectorAll('[data-testid="cds-reason-row"]');
    return { fixture, editor: fixture.componentInstance, el, reasons, click, rows };
  }

  it('lists the standard reasons while the service has no list of its own', () => {
    const { el, rows } = render();
    const defaults = el.querySelector('[data-testid="cds-reasons-defaults"]')!;
    expect(defaults.querySelectorAll('li').length).toBe(CDS_DEFAULT_OVERRIDE_REASONS.length);
    expect(defaults.textContent).toContain('will-monitor');
    expect(rows().length).toBe(0);
  });

  it('"Change the reasons" starts an own list from the standard ones', () => {
    const { click, rows, reasons, el } = render();
    click('[data-testid="cds-reasons-customize"]');

    expect(rows().length).toBe(CDS_DEFAULT_OVERRIDE_REASONS.length);
    expect(reasons.getRawValue()).toEqual(CDS_DEFAULT_OVERRIDE_REASONS.map(r => ({ ...r })));
    expect(reasons.dirty).toBeTrue();
    expect((el.querySelector('#cds-reason-code-0') as HTMLInputElement).value).toBe('patient-tolerated');
  });

  it(`adds up to ${CDS_OVERRIDE_REASON_MAX} reasons`, () => {
    const { click, rows, el, editor, reasons } = render();
    click('[data-testid="cds-reasons-customize"]');
    while (el.querySelector('[data-testid="cds-reason-add"]')) click('[data-testid="cds-reason-add"]');

    expect(rows().length).toBe(CDS_OVERRIDE_REASON_MAX);
    editor.add();
    expect(reasons.length).toBe(CDS_OVERRIDE_REASON_MAX);
  });

  it('keeps at least one reason in an own list', () => {
    const array = buildCdsReasonsArray(null);
    array.push(makeCdsReasonGroup({ code: 'only', display: 'Only one' }));
    const { el, editor, reasons } = render(array);

    expect(el.querySelector('[aria-label="Remove reason 1"]')).toBeNull();
    editor.remove(0);
    expect(reasons.length).toBe(1);
  });

  it('shows the code rule and the duplicate message', () => {
    const array = buildCdsReasonsArray(null);
    array.push(makeCdsReasonGroup({ code: 'has space', display: 'A' }));
    array.push(makeCdsReasonGroup({ code: 'dup', display: 'B' }));
    array.push(makeCdsReasonGroup({ code: 'DUP', display: 'C' }));
    const { el, fixture } = render(array);
    expect(el.textContent).not.toContain('Use 1 to 50 letters');

    fixture.componentRef.setInput('showAllErrors', true);
    fixture.detectChanges();

    expect(el.textContent).toContain('Use 1 to 50 letters, digits, hyphens or underscores, with no spaces.');
    expect(el.textContent).toContain('The code "DUP" is used twice.');
  });

  it('goes back to the standard reasons', () => {
    const array = buildCdsReasonsArray(null);
    array.push(makeCdsReasonGroup({ code: 'a', display: 'A' }));
    const { el, fixture, reasons } = render(array);
    const back = Array.from(el.querySelectorAll('button')).find(b => b.textContent?.includes('Go back to the standard reasons'))!;
    back.click();
    fixture.detectChanges();

    expect(reasons.length).toBe(0);
    expect(el.querySelector('[data-testid="cds-reasons-defaults"]')).not.toBeNull();
  });

  it('offers no edit buttons when disabled', () => {
    const array = buildCdsReasonsArray(null);
    array.disable();
    const { el, editor } = render(array);
    expect(el.querySelector('[data-testid="cds-reasons-customize"]')).toBeNull();
    editor.customize();
    expect(array.length).toBe(0);
  });
});
