import { inject } from '@angular/core';
import { CanDeactivateFn } from '@angular/router';
import { Observable, of } from 'rxjs';
import { UnsavedChangesPromptService } from '../services/unsaved-changes-prompt.service';
import { HasUnsavedChanges } from './has-unsaved-changes';

// Applies to every route whose component implements HasUnsavedChanges — see that interface for
// what each component needs to expose. Blocks/confirms navigation away (sidebar clicks, browser
// back, programmatic router.navigate) uniformly across the app.
export const unsavedChangesGuard: CanDeactivateFn<HasUnsavedChanges> = (component): Observable<boolean> => {
  if (!component) return of(true);
  return inject(UnsavedChangesPromptService).confirmLeave(component);
};
