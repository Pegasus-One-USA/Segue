import { Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { Observable, of } from 'rxjs';
import { UnsavedChangesPromptService } from './unsaved-changes-prompt.service';
import { UnsavedChangesRegistryService } from './unsaved-changes-registry.service';
import { ToastService } from '../../services/toast.service';
import { AuthStore } from '../../auth/store/auth.store';
import { HasUnsavedChanges } from '../guards/has-unsaved-changes';

/** A page that can save itself (HasUnsavedChanges.saveBeforeLeave — the Branding page) gets the same "Leave this
 *  page?" modal with a Save button added: Save saves and leaves only on success, Leave discards, Cancel stays.
 *  Every page without it — the Mapping screen's popover and route among them — keeps the exact Leave/Cancel modal. */
@Component({ standalone: true, template: '' })
class RegisteredPopover {
  constructor() {
    inject(UnsavedChangesRegistryService).register(() => true);
  }
}

describe('UnsavedChangesPromptService — Save on the leave prompt', () => {
  let dialog: jasmine.SpyObj<MatDialog>;
  let service: UnsavedChangesPromptService;

  const ORIGINAL_PROMPT = {
    title: 'Leave this page?',
    message: 'You have unsaved changes that will be lost if you leave. Continue?',
    confirmLabel: 'Leave',
    danger: true,
  };

  function answer(choice: boolean | 'save' | undefined): void {
    dialog.open.and.returnValue({ afterClosed: () => of(choice) } as never);
  }

  function leave(page: HasUnsavedChanges): boolean | undefined {
    let result: boolean | undefined;
    service.confirmLeavePage(page).subscribe(r => (result = r));
    return result;
  }

  const promptData = () => (dialog.open.calls.mostRecent().args[1] as { data: object }).data;

  function saveablePage(saveResult: Observable<boolean>) {
    return { hasUnsavedChanges: () => true, saveBeforeLeave: jasmine.createSpy('saveBeforeLeave').and.returnValue(saveResult) };
  }

  beforeEach(() => {
    dialog = jasmine.createSpyObj<MatDialog>('MatDialog', ['open']);
    TestBed.configureTestingModule({
      providers: [
        { provide: MatDialog, useValue: dialog },
        { provide: ToastService, useValue: jasmine.createSpyObj<ToastService>('ToastService', ['warning']) },
        { provide: AuthStore, useValue: { isAuthenticated: () => true } },
      ],
    });
    service = TestBed.inject(UnsavedChangesPromptService);
  });

  it('a page without saveBeforeLeave (e.g. Mapping) gets the original Leave/Cancel modal, unchanged', () => {
    answer(true);

    expect(leave({ hasUnsavedChanges: () => true })).toBeTrue();
    expect(promptData()).toEqual(ORIGINAL_PROMPT);
  });

  it('a saveable page gets the same modal plus a Save button and the "Page data has been modified" message', () => {
    answer(false);
    leave(saveablePage(of(true)));

    expect(promptData()).toEqual({
      ...ORIGINAL_PROMPT,
      message: 'Page data has been modified. Do you want to save your changes before leaving?',
      saveLabel: 'Save',
    });
  });

  it('Save: saves, then leaves when the save succeeds', () => {
    answer('save');
    const page = saveablePage(of(true));

    expect(leave(page)).toBeTrue();
    expect(page.saveBeforeLeave).toHaveBeenCalledTimes(1);
  });

  it('Save: stays on the page when the save fails', () => {
    answer('save');

    expect(leave(saveablePage(of(false)))).toBeFalse();
  });

  it('Leave: navigates without saving', () => {
    answer(true);
    const page = saveablePage(of(true));

    expect(leave(page)).toBeTrue();
    expect(page.saveBeforeLeave).not.toHaveBeenCalled();
  });

  for (const [label, choice] of [['Cancel', false], ['Esc / backdrop', undefined]] as const) {
    it(`${label}: stays on the page without saving`, () => {
      answer(choice);
      const page = saveablePage(of(true));

      expect(leave(page)).toBeFalse();
      expect(page.saveBeforeLeave).not.toHaveBeenCalled();
    });
  }

  it('a clean saveable page with dirt only in something registered on it (a popover) gets the plain modal', () => {
    TestBed.createComponent(RegisteredPopover);
    answer(false);
    const page = { hasUnsavedChanges: () => false, saveBeforeLeave: jasmine.createSpy('saveBeforeLeave') };

    leave(page);

    expect(promptData()).toEqual(ORIGINAL_PROMPT);
    expect(page.saveBeforeLeave).not.toHaveBeenCalled();
  });
});
