import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { CdsSandboxHintComponent } from './cds-sandbox-hint.component';

describe('CdsSandboxHintComponent', () => {
  function render(inputs: { discoveryUrl?: string | null; canAdd?: boolean; alreadyAdded?: boolean }) {
    TestBed.configureTestingModule({ imports: [CdsSandboxHintComponent], providers: [provideNoopAnimations()] });
    const fixture = TestBed.createComponent(CdsSandboxHintComponent);
    for (const [k, v] of Object.entries(inputs)) fixture.componentRef.setInput(k, v);
    fixture.detectChanges();
    return { component: fixture.componentInstance, el: fixture.nativeElement as HTMLElement };
  }

  it('shows the sandbox issuer and JWKS URL, and the discovery URL to paste', () => {
    const { el } = render({ discoveryUrl: 'https://segue.example.org/cds-services' });

    expect(el.textContent).toContain('https://sandbox.cds-hooks.org/.well-known/jwks.json');
    expect(el.textContent).toContain('https://segue.example.org/cds-services');
  });

  it('hides the add button without create permission', () => {
    const { el } = render({ canAdd: false });
    expect(el.querySelector('[data-testid="cds-add-sandbox"]')).toBeNull();
  });

  it('emits addSandbox from the button, and shows a badge instead once added', () => {
    const { component, el } = render({ canAdd: true });
    const emitted = jasmine.createSpy('addSandbox');
    component.addSandbox.subscribe(emitted);

    (el.querySelector('[data-testid="cds-add-sandbox"]') as HTMLButtonElement).click();
    expect(emitted).toHaveBeenCalled();

    TestBed.resetTestingModule();
    const added = render({ canAdd: true, alreadyAdded: true });
    expect(added.el.querySelector('[data-testid="cds-add-sandbox"]')).toBeNull();
    expect(added.el.textContent).toContain('Sandbox already trusted');
  });
});
