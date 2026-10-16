import { TestBed } from '@angular/core/testing';
import { FormControl } from '@angular/forms';
import { provideRouter } from '@angular/router';
import { CdsAllowedIssuersComponent } from './cds-allowed-issuers.component';
import { CdsTrustedIssuer } from '../../models/cds.models';
import { makeIssuer } from '../../testing/cds-test-data';

describe('CdsAllowedIssuersComponent', () => {
  const A = makeIssuer({ id: 'a', name: 'Epic' });
  const B = makeIssuer({ id: 'b', name: 'Sandbox', isEnabled: false });

  function render(issuers: CdsTrustedIssuer[], ids: string[] = []) {
    TestBed.configureTestingModule({ imports: [CdsAllowedIssuersComponent], providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(CdsAllowedIssuersComponent);
    const control = new FormControl<string[]>(ids, { nonNullable: true });
    fixture.componentRef.setInput('issuers', issuers);
    fixture.componentRef.setInput('control', control);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, el, control, boxes: () => Array.from(el.querySelectorAll('input[type="checkbox"]')) as HTMLInputElement[] };
  }

  it('links to Connect your EHR when there are no issuers', () => {
    const { el } = render([]);
    expect(el.querySelector('a')?.getAttribute('href')).toBe('/clinical-decision-support/connect');
  });

  it('says any enabled issuer may call while nothing is ticked, and marks issuers that are off', () => {
    const { el, boxes } = render([A, B]);
    expect(el.textContent).toContain('Any enabled trusted issuer can call this service.');
    expect(boxes().every(b => !b.checked)).toBeTrue();
    expect(el.querySelectorAll('.issuer__off').length).toBe(1);
  });

  it('ticking adds to the allow-list and marks the control dirty; unticking removes', () => {
    const { fixture, el, control, boxes } = render([A, B]);

    boxes()[1].click();
    fixture.detectChanges();
    expect(control.value).toEqual(['b']);
    expect(control.dirty).toBeTrue();
    expect(el.textContent).toContain('Only the ticked issuers can call this service.');

    boxes()[1].click();
    expect(control.value).toEqual([]);
  });

  it('shows ids of deleted issuers so they can be cleared', () => {
    const { fixture, el, control } = render([A], ['a', 'gone']);
    expect(el.querySelectorAll('.issuer--missing').length).toBe(1);

    (el.querySelector('.issuer--missing input') as HTMLInputElement).click();
    fixture.detectChanges();
    expect(control.value).toEqual(['a']);
  });

  it('shows the allow-list read-only when the control is disabled', () => {
    const { fixture, control, boxes } = render([A], ['a']);
    control.disable();
    fixture.detectChanges();
    expect(boxes().every(b => b.disabled)).toBeTrue();
  });
});
