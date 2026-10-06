import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { UnsavedChangesPromptService } from './unsaved-changes-prompt.service';
import { ToastService } from '../../services/toast.service';
import { AuthStore } from '../../auth/store/auth.store';

/**
 * The "leave this page?" prompt opens WHILE a navigation is in flight, so GlobalLoaderComponent is
 * already showing — it starts on NavigationStart and only stops once navigation resolves, which this
 * very dialog is what resolves. The loader's backdrop deliberately sits above Angular CDK's overlay
 * stack (--z-loading-overlay 1050 vs CDK's 1000, so a load started from inside a dialog blocks that
 * dialog's own fields). In this direction that ordering buries the prompt: its buttons cannot be
 * clicked, navigation never resolves, the loader never stops, and the page is stuck.
 *
 * The prompt therefore carries a panel class the stylesheet uses to lift the CDK container above the
 * loader for exactly as long as this one dialog is open — the same escape the session-expired prompt
 * already makes for the same reason.
 */
describe('UnsavedChangesPromptService — leave prompt stacking', () => {
  let dialog: jasmine.SpyObj<MatDialog>;
  let service: UnsavedChangesPromptService;

  beforeEach(() => {
    dialog = jasmine.createSpyObj<MatDialog>('MatDialog', ['open']);
    dialog.open.and.returnValue({ afterClosed: () => of(true) } as never);

    TestBed.configureTestingModule({
      providers: [
        UnsavedChangesPromptService,
        { provide: MatDialog, useValue: dialog },
        { provide: ToastService, useValue: jasmine.createSpyObj('ToastService', ['warning']) },
        { provide: AuthStore, useValue: { isAuthenticated: () => true } },
      ],
    });

    service = TestBed.inject(UnsavedChangesPromptService);
  });

  it('opens the leave prompt with the panel class that lifts it above the busy loader', () => {
    service.confirmLeave({ hasUnsavedChanges: () => true }).subscribe();

    expect(dialog.open).toHaveBeenCalledTimes(1);
    const config = dialog.open.calls.mostRecent().args[1] as { panelClass?: string | string[] };
    const panelClass = Array.isArray(config.panelClass) ? config.panelClass : [config.panelClass];

    expect(panelClass)
      .withContext(
        'without this class the prompt renders under the global loader that the pending navigation '
        + 'itself raised, so neither button can be clicked and the page never recovers')
      .toContain('leave-confirm-dialog');
  });

  it('renders that prompt above the global loading overlay', () => {
    // The wiring test above only proves the class is sent. This proves the class does something: the
    // real stylesheet is loaded into the Karma page (angular.json test styles), so the computed
    // z-index is the one the browser will actually use.
    const container = document.createElement('div');
    container.className = 'cdk-overlay-container';
    container.innerHTML = '<div class="leave-confirm-dialog"></div>';
    document.body.appendChild(container);

    const loader = document.createElement('div');
    loader.style.zIndex = getComputedStyle(document.documentElement).getPropertyValue('--z-loading-overlay');
    document.body.appendChild(loader);

    try {
      const promptZ = Number(getComputedStyle(container).zIndex);
      const loaderZ = Number(loader.style.zIndex);

      expect(loaderZ).withContext('--z-loading-overlay must be defined for this to mean anything').toBeGreaterThan(0);
      expect(promptZ)
        .withContext(`leave prompt at ${promptZ} must outrank the busy loader at ${loaderZ}`)
        .toBeGreaterThan(loaderZ);
    } finally {
      container.remove();
      loader.remove();
    }
  });

  it('leaves every OTHER dialog under the loader, which is deliberate', () => {
    // A load started from inside a dialog must still block that dialog's own fields — so only the
    // container holding the leave prompt is lifted, not the CDK stack as a whole.
    const container = document.createElement('div');
    container.className = 'cdk-overlay-container';
    container.innerHTML = '<div class="some-other-dialog"></div>';
    document.body.appendChild(container);

    try {
      const z = getComputedStyle(container).zIndex;
      expect(z === 'auto' || Number(z) < 1050)
        .withContext(`an unrelated overlay container should not be lifted, got ${z}`)
        .toBeTrue();
    } finally {
      container.remove();
    }
  });

  it('does not prompt at all when there is nothing unsaved', () => {
    let proceeded: boolean | undefined;
    service.confirmLeave({ hasUnsavedChanges: () => false }).subscribe(v => (proceeded = v));

    expect(dialog.open).not.toHaveBeenCalled();
    expect(proceeded).toBeTrue();
  });
});
