import { Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { UnsavedChangesPromptService } from './unsaved-changes-prompt.service';
import { UnsavedChangesRegistryService } from './unsaved-changes-registry.service';
import { ToastService } from '../../services/toast.service';
import { AuthStore } from '../../auth/store/auth.store';

/** A popover or panel open on a page can have unsaved edits its page knows nothing about (the mapping popover
 *  keeps them until its own Save). The leave prompt now also asks the registry, so registering one check is
 *  all such a component needs to be covered when the page is left. */
@Component({ standalone: true, template: '' })
class DirtyPanelComponent {
  dirty = true;
  constructor() {
    inject(UnsavedChangesRegistryService).register(() => this.dirty);
  }
}

describe('UnsavedChangesPromptService — registered checks', () => {
  let dialog: jasmine.SpyObj<MatDialog>;
  let service: UnsavedChangesPromptService;
  const cleanPage = { hasUnsavedChanges: () => false };

  beforeEach(() => {
    dialog = jasmine.createSpyObj<MatDialog>('MatDialog', ['open']);
    dialog.open.and.returnValue({ afterClosed: () => of(false) } as never);
    TestBed.configureTestingModule({
      providers: [
        { provide: MatDialog, useValue: dialog },
        { provide: ToastService, useValue: jasmine.createSpyObj('ToastService', ['warning']) },
        { provide: AuthStore, useValue: { isAuthenticated: () => true } },
      ],
    });
    service = TestBed.inject(UnsavedChangesPromptService);
  });

  it('lets a clean page go when nothing on it is registered dirty', () => {
    let result: boolean | undefined;
    service.confirmLeave(cleanPage).subscribe(r => (result = r));
    expect(dialog.open).not.toHaveBeenCalled();
    expect(result).toBeTrue();
  });

  it('asks when the page is clean but a component on it has unsaved edits', () => {
    TestBed.createComponent(DirtyPanelComponent);
    let result: boolean | undefined;
    service.confirmLeave(cleanPage).subscribe(r => (result = r));
    expect(dialog.open).toHaveBeenCalledTimes(1);
    expect(result).toBeFalse();   // Cancel
  });

  it('stops asking once that component is gone', () => {
    TestBed.createComponent(DirtyPanelComponent).destroy();
    service.confirmLeave(cleanPage).subscribe();
    expect(dialog.open).not.toHaveBeenCalled();
  });

  it('confirmDiscard opens the same "Leave this page?" modal', () => {
    service.confirmDiscard().subscribe();
    const data = (dialog.open.calls.mostRecent().args[1] as { data: Record<string, unknown> }).data;
    expect(data['title']).toBe('Leave this page?');
    expect(data['message']).toBe('You have unsaved changes that will be lost if you leave. Continue?');
    expect(data['confirmLabel']).toBe('Leave');
  });
});
