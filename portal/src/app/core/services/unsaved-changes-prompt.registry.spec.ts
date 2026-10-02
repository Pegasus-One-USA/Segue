import { Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { UnsavedChangesPromptService } from './unsaved-changes-prompt.service';
import { UnsavedChangesRegistryService } from './unsaved-changes-registry.service';
import { ToastService } from '../../services/toast.service';
import { AuthStore } from '../../auth/store/auth.store';

/** A popover or panel open on a page can have unsaved edits its page knows nothing about (the mapping popover
 *  keeps them until its own Save). Leaving the PAGE — confirmLeavePage, used by the route guard and sign out —
 *  asks about those too. confirmLeave stays scoped to the one component it is given, because it also guards
 *  closing a dialog or a form: asking the registry there prompted for some other component's edits. */
@Component({ standalone: true, template: '' })
class RegisteredComponent {
  dirty = false;
  saving = false;
  constructor() {
    inject(UnsavedChangesRegistryService).register(() => this.dirty, undefined, () => this.saving);
  }
}

describe('UnsavedChangesPromptService — page vs component', () => {
  let dialog: jasmine.SpyObj<MatDialog>;
  let toast: jasmine.SpyObj<ToastService>;
  let service: UnsavedChangesPromptService;
  const clean = { hasUnsavedChanges: () => false };

  beforeEach(() => {
    dialog = jasmine.createSpyObj<MatDialog>('MatDialog', ['open']);
    dialog.open.and.returnValue({ afterClosed: () => of(false) } as never);
    toast = jasmine.createSpyObj<ToastService>('ToastService', ['warning']);
    TestBed.configureTestingModule({
      providers: [
        { provide: MatDialog, useValue: dialog },
        { provide: ToastService, useValue: toast },
        { provide: AuthStore, useValue: { isAuthenticated: () => true } },
      ],
    });
    service = TestBed.inject(UnsavedChangesPromptService);
  });

  const registered = (state: Partial<Pick<RegisteredComponent, 'dirty' | 'saving'>>) =>
    Object.assign(TestBed.createComponent(RegisteredComponent).componentInstance, state);

  it('confirmLeave: closing a clean dialog or form does not ask, even while something else is dirty', () => {
    registered({ dirty: true });   // e.g. the workflow builder behind an EHR source form
    let result: boolean | undefined;
    service.confirmLeave(clean).subscribe(r => (result = r));
    expect(dialog.open).not.toHaveBeenCalled();
    expect(result).toBeTrue();
  });

  it('confirmLeavePage: a clean page with nothing registered dirty leaves freely', () => {
    registered({});
    let result: boolean | undefined;
    service.confirmLeavePage(clean).subscribe(r => (result = r));
    expect(dialog.open).not.toHaveBeenCalled();
    expect(result).toBeTrue();
  });

  it('confirmLeavePage: asks when the page is clean but something on it has unsaved edits', () => {
    registered({ dirty: true });
    let result: boolean | undefined;
    service.confirmLeavePage(clean).subscribe(r => (result = r));
    expect(dialog.open).toHaveBeenCalledTimes(1);
    expect(result).toBeFalse();   // Cancel
  });

  it('confirmLeavePage: a save in flight on something registered blocks with "please wait", never "Leave"', () => {
    registered({ saving: true });
    let result: boolean | undefined;
    service.confirmLeavePage(clean).subscribe(r => (result = r));
    expect(toast.warning).toHaveBeenCalledWith('Please wait for the current save to finish before leaving this page.');
    expect(dialog.open).not.toHaveBeenCalled();
    expect(result).toBeFalse();
  });

  it('stops asking once the registered component is gone', () => {
    const fixture = TestBed.createComponent(RegisteredComponent);
    fixture.componentInstance.dirty = true;
    fixture.destroy();
    service.confirmLeavePage(clean).subscribe();
    expect(dialog.open).not.toHaveBeenCalled();
  });

  it('the registry still reports a save in flight for the tab-close warning', () => {
    registered({ saving: true });
    const registry = TestBed.inject(UnsavedChangesRegistryService);
    expect(registry.hasAnyUnsavedChanges()).toBeTrue();
    expect(registry.hasAnyUnsavedEdits()).toBeFalse();
  });

  it('confirmDiscard opens the "Leave this page?" modal', () => {
    service.confirmDiscard().subscribe();
    const data = (dialog.open.calls.mostRecent().args[1] as { data: Record<string, unknown> }).data;
    expect(data['title']).toBe('Leave this page?');
    expect(data['message']).toBe('You have unsaved changes that will be lost if you leave. Continue?');
    expect(data['confirmLabel']).toBe('Leave');
  });
});
