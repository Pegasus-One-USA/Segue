import { TestBed, fakeAsync, flushMicrotasks, tick } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { CdsCopyButtonComponent } from './cds-copy-button.component';
import { ToastService } from '../../../services/toast.service';
import { makeToastSpy } from '../../testing/cds-test-data';

describe('CdsCopyButtonComponent', () => {
  let toast: ReturnType<typeof makeToastSpy>;

  function render() {
    toast = makeToastSpy();
    TestBed.configureTestingModule({
      imports: [CdsCopyButtonComponent],
      providers: [provideNoopAnimations(), { provide: ToastService, useValue: toast }],
    });
    const fixture = TestBed.createComponent(CdsCopyButtonComponent);
    fixture.componentRef.setInput('value', 'https://segue.example.org/cds-services');
    fixture.componentRef.setInput('what', 'discovery URL');
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('copies the value and shows a tick for a moment', fakeAsync(() => {
    const write = spyOn(navigator.clipboard, 'writeText').and.returnValue(Promise.resolve());
    const { fixture, el } = render();
    expect(el.querySelector('button')?.getAttribute('aria-label')).toBe('Copy discovery URL');

    (el.querySelector('button') as HTMLButtonElement).click();
    flushMicrotasks();
    fixture.detectChanges();

    expect(write).toHaveBeenCalledWith('https://segue.example.org/cds-services');
    expect(el.querySelector('mat-icon')?.textContent).toContain('check');

    tick(1500);
    fixture.detectChanges();
    expect(el.querySelector('mat-icon')?.textContent).toContain('content_copy');
  }));

  it('shows an error toast when the clipboard refuses', fakeAsync(() => {
    spyOn(navigator.clipboard, 'writeText').and.returnValue(Promise.reject(new Error('denied')));
    const { el } = render();

    (el.querySelector('button') as HTMLButtonElement).click();
    flushMicrotasks();

    expect(toast.error).toHaveBeenCalledWith('Could not copy to the clipboard.');
  }));
});
