import { TestBed } from '@angular/core/testing';
import { CdsHandlerPickerDialogComponent } from './cds-handler-picker-dialog.component';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { CdsHandler } from '../../models/cds.models';
import { makeHandler } from '../../testing/cds-test-data';

describe('CdsHandlerPickerDialogComponent', () => {
  function render(handlers: CdsHandler[]) {
    const ref = { close: jasmine.createSpy('close') };
    TestBed.configureTestingModule({
      imports: [CdsHandlerPickerDialogComponent],
      providers: [{ provide: DialogRef, useValue: ref }, { provide: DIALOG_DATA, useValue: { handlers } }],
    });
    const fixture = TestBed.createComponent(CdsHandlerPickerDialogComponent);
    fixture.detectChanges();
    return { ref, el: fixture.nativeElement as HTMLElement };
  }

  it('lists each check with its hooks and closes with the picked key', () => {
    const { ref, el } = render([makeHandler(), makeHandler({ key: 'care-gap', displayName: 'Care gaps', supportedHooks: ['patient-view'] })]);

    const careGap = el.querySelector('[data-testid="cds-handler-care-gap"]') as HTMLButtonElement;
    expect(careGap.textContent).toContain('Care gaps');
    expect(careGap.textContent).toContain('Patient chart opened');

    careGap.click();
    expect(ref.close).toHaveBeenCalledWith('care-gap');
  });

  it('says so when no checks are available', () => {
    expect(render([]).el.textContent).toContain('No checks are available.');
  });
});
