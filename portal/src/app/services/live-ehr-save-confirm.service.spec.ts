import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { LiveEhrSaveConfirmService } from './live-ehr-save-confirm.service';
import { LiveEhrWriteTarget } from './live-ehr-write-targets.util';
import { DialogService } from '../core/services/dialog.service';

const epic: LiveEhrWriteTarget = { nodeId: 'n1', vendor: 'Epic', connectionId: 'c-1' };
const athena: LiveEhrWriteTarget = { nodeId: 'n2', vendor: 'Athenahealth', connectionId: 'c-2' };

/** Saving a workflow that adds live EHR writing asks once, naming every EHR it writes into. */
describe('LiveEhrSaveConfirmService', () => {
  let dialogs: { open: jasmine.Spy };
  let closed: Subject<boolean | undefined>;
  let service: LiveEhrSaveConfirmService;

  beforeEach(() => {
    closed = new Subject<boolean | undefined>();
    dialogs = { open: jasmine.createSpy('open').and.returnValue({ afterClosed: () => closed.asObservable() }) };
    TestBed.configureTestingModule({ providers: [{ provide: DialogService, useValue: dialogs }] });
    service = TestBed.inject(LiveEhrSaveConfirmService);
  });

  const answer = (saved: LiveEhrWriteTarget[], saving: LiveEhrWriteTarget[]) => {
    let result: boolean | undefined;
    service.confirmSave(saved, saving).subscribe(r => (result = r));
    return () => result;
  };

  it('saves without asking when nothing live is added', () => {
    expect(answer([epic], [epic])()).toBeTrue();
    expect(answer([], [])()).toBeTrue();
    expect(dialogs.open).not.toHaveBeenCalled();
  });

  it('asks about every live EHR, and Save goes ahead', () => {
    const result = answer([epic], [epic, athena]);
    const data = dialogs.open.calls.mostRecent().args[1].data;
    expect(data.title).toBe('Write into Epic and athenahealth on every run?');
    expect(data.message).toBe('This workflow will write records into Epic and athenahealth every time it runs, including scheduled and triggered runs. This cannot be undone.');
    expect(data.confirmLabel).toBe('Save');
    expect(result()).toBeUndefined();
    closed.next(true);
    expect(result()).toBeTrue();
  });

  it('Cancel, or closing the question, does not save', () => {
    const cancelled = answer([], [epic]);
    closed.next(false);
    expect(cancelled()).toBeFalse();

    closed = new Subject<boolean | undefined>();
    const dismissed = answer([], [epic]);
    closed.next(undefined);
    expect(dismissed()).toBeFalse();
  });
});
