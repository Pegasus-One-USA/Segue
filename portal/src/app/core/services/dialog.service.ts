import { Injectable, InjectionToken, Injector, Type, inject, signal } from '@angular/core';
import { Router, NavigationStart } from '@angular/router';
import { Observable, Subject, filter } from 'rxjs';
import { UnsavedChangesPromptService } from './unsaved-changes-prompt.service';
import { HasUnsavedChanges } from '../guards/has-unsaved-changes';

/** Custom equivalent of Angular Material's MAT_DIALOG_DATA — injected into whatever component
 *  DialogService.open() renders, carrying the `data` object passed to that call. */
export const DIALOG_DATA = new InjectionToken<unknown>('DIALOG_DATA');

export interface DialogConfig<D = unknown> {
  data?: D;
  /** When true, Escape and backdrop-click do nothing — matches MatDialog's own disableClose,
   *  which every dialog opener in this app already sets today. */
  disableClose?: boolean;
  /** Non-maximized width, e.g. '560px'. Height/positioning is otherwise automatic. */
  width?: string;
  /** Shows the maximize/restore toggle in the shell's chrome. Off by default — most dialogs
   *  (Role, User, Settings forms) are short and don't need it; only complex, many-step dialogs
   *  (the workflow builder's node config) should opt in. */
  maximizable?: boolean;
  /** Opens already maximized instead of requiring a manual click first — for dialogs whose content
   *  is dense/control-heavy enough (e.g. a destination connection's per-type form) that the
   *  non-maximized width is cramped by default. Ignored unless maximizable is also true. */
  startMaximized?: boolean;
  /** Fills the entire content area (edge-to-edge, no rounded card, no visible backdrop margin) in its
   *  DEFAULT (non-maximized) state — the same treatment NodeLibraryDialogComponent's `.nld` always
   *  gives the Source Connection screen (`position: absolute; inset: 0; border-radius: 0` against the
   *  already content-area-scoped backdrop). `width` is ignored when this is set. Still independent of
   *  `maximizable`: toggling maximize still escalates further, to the true full viewport (sidebar and
   *  all) via `position: fixed`. */
  fillContent?: boolean;
}

/** True when `x` looks like an Angular FormGroup/FormControl — specifically the one thing
 *  attemptDirtyClose's generic fallback needs from it, so nothing here has to import
 *  `@angular/forms` or care which of FormGroup/FormControl/FormArray it actually is. */
function looksLikeAngularForm(x: unknown): x is { dirty: boolean } {
  return !!x && typeof x === 'object' && typeof (x as { dirty?: unknown }).dirty === 'boolean';
}

/** Custom equivalent of Angular Material's MatDialogRef — injected into the opened component so it
 *  can close itself and report a result, same call shape (`this.dialogRef.close(result)`) as every
 *  MatDialog-based dialog in this app already uses, so migrating a dialog off MatDialog onto this
 *  service only ever touches its DI tokens, never its own close-with-result logic. */
export class DialogRef<R = unknown> {
  private readonly _afterClosed = new Subject<R | undefined>();

  /** Set by DialogShellComponent once NgComponentOutlet actually renders the dialog component —
   *  unavailable at construction time (DialogService.open() creates this DialogRef before that
   *  component exists), same two-step lifecycle MatDialogRef's own `componentInstance` has. */
  componentInstance?: unknown;

  /** Whether the panel currently fills the content area edge-to-edge (see DialogConfig.fillContent for
   *  what that means) — a live signal, not a one-time config value, so a dialog whose content changes
   *  shape mid-flight (e.g. Mapping Profile only shows its field-mapping canvas once a resource type is
   *  picked) can toggle this itself via `dialogRef.fillContent.set(...)` as that happens, rather than
   *  committing to one size for the dialog's whole lifetime up front. */
  readonly fillContent: ReturnType<typeof signal<boolean>>;

  constructor(
    private readonly onClose: () => void,
    private readonly unsavedChangesPrompt: UnsavedChangesPromptService,
    initialFillContent = false,
  ) {
    this.fillContent = signal(initialFillContent);
  }

  close(result?: R): void {
    this.onClose();
    this._afterClosed.next(result);
    this._afterClosed.complete();
  }

  afterClosed(): Observable<R | undefined> {
    return this._afterClosed.asObservable();
  }

  /** The single close-attempt entry point every dialog and DialogShellComponent's own ×/Esc/backdrop
   *  should call instead of `close()` directly, so "was this actually edited?" is answered the SAME
   *  way everywhere rather than each dialog hand-rolling its own version of this check:
   *  1. If the component implements HasUnsavedChanges (an explicit `hasUnsavedChanges()`), that wins
   *     — for dialogs whose real "did anything change" question isn't just one form (e.g. Destination
   *     Connection also counts having picked a connection type; Mapping Profile also counts the
   *     mapping canvas's row count).
   *  2. Otherwise, fall back to duck-typing a `form` property that looks like an Angular form and
   *     read its own `.dirty` — covers every plain single-reactive-form dialog (the common case) with
   *     zero per-dialog boilerplate: no injected service, no hand-written method, nothing to forget.
   *  3. No form and no override → nothing to lose, close immediately.
   *  Either way, the confirmation itself (if needed) is the same shared "Discard changes?" dialog
   *  every other unsaved-changes prompt in the app already uses. */
  attemptClose(): void {
    const instance = this.componentInstance;
    const explicit = instance as Partial<HasUnsavedChanges> | undefined;
    const hasUnsavedChanges: (() => boolean) | undefined =
      explicit && typeof explicit.hasUnsavedChanges === 'function'
        ? () => explicit.hasUnsavedChanges!()
        : looksLikeAngularForm((instance as { form?: unknown } | undefined)?.form)
          ? () => (instance as { form: { dirty: boolean } }).form.dirty
          : undefined;

    if (!hasUnsavedChanges) {
      this.close(undefined);
      return;
    }

    const checkable: HasUnsavedChanges = {
      hasUnsavedChanges,
      isSaveInProgress: typeof explicit?.isSaveInProgress === 'function'
        ? () => explicit.isSaveInProgress!()
        : undefined,
    };

    this.unsavedChangesPrompt.confirmLeave(checkable).subscribe(canClose => {
      if (canClose) this.close(undefined);
    });
  }
}

export interface DialogEntry {
  id: number;
  component: Type<unknown>;
  injector: Injector;
  dialogRef: DialogRef<unknown>;
  disableClose: boolean;
  width?: string;
  maximizable: boolean;
  /** Per-entry maximize state — a signal (not a plain boolean) so DialogShellComponent's template
   *  re-renders on toggle without needing its own wrapper state. */
  isMaximized: ReturnType<typeof signal<boolean>>;
}

/**
 * Renders dialogs inline in the app's own component tree (inside .shell-content, via
 * DialogOutletComponent mounted once in app-shell.component.html) instead of Angular Material's
 * MatDialog, which attaches its CDK overlay straight to document.body — outside app-shell entirely,
 * which is why a MatDialog's backdrop can never be scoped to just the content area the way
 * app-modal-overlay's now is. This service's own backdrop (DialogShellComponent) inherits that
 * scoping for free by living in the same DOM subtree.
 *
 * `open()` has the same shape as MatDialog.open() (component + config, returns a ref with
 * `.afterClosed()`) so callers barely change; the DIALOG_DATA/DialogRef tokens mirror
 * MAT_DIALOG_DATA/MatDialogRef so a dialog component's own internals barely change either — only
 * its imports and injected tokens do.
 */
@Injectable({ providedIn: 'root' })
export class DialogService {
  private readonly rootInjector = inject(Injector);
  private readonly unsavedChangesPrompt = inject(UnsavedChangesPromptService);
  private nextId = 0;

  /** Read by DialogOutletComponent to render the current stack — supports more than one dialog open
   *  at once (e.g. a confirm dialog opened from within another dialog), each getting its own shell. */
  readonly stack = signal<DialogEntry[]>([]);

  /** .shell-content's scrollTop at the moment the FIRST dialog in a stack opened — restored when the
   *  stack empties back out. Without this, app-dialog-backdrop (position:absolute against .shell-content,
   *  see dialog-shell.component.scss) renders at .shell-content's scrolled-past-top edge whenever the
   *  page was scrolled down before opening, landing the whole dialog off-screen above the visible
   *  viewport until the user scrolls back up to find it. */
  private scrollLockOriginalTop = 0;

  constructor() {
    // DialogOutletComponent lives in app-shell (see its own doc comment), a sibling of
    // <router-outlet> that never gets destroyed on navigation — so without this, a dialog opened
    // from one page keeps floating over whatever page you navigate to next, since nothing about a
    // route change otherwise touches this service's stack. NavigationStart (not NavigationEnd): close
    // before the new page's content swaps in, so there's no frame where a stale dialog briefly
    // overlaps it.
    const router = inject(Router);
    router.events.pipe(filter(e => e instanceof NavigationStart)).subscribe(() => {
      for (const entry of this.stack()) entry.dialogRef.close(undefined);
    });
  }

  open<C, D = unknown, R = unknown>(component: Type<C>, config: DialogConfig<D> = {}): DialogRef<R> {
    const id = this.nextId++;
    const dialogRef = new DialogRef<R>(() => {
      this.stack.update(entries => entries.filter(e => e.id !== id));
      if (this.stack().length === 0) this._unlockContentScroll();
    }, this.unsavedChangesPrompt, config.fillContent ?? false);

    if (this.stack().length === 0) this._lockContentScroll();

    const injector = Injector.create({
      parent: this.rootInjector,
      providers: [
        { provide: DIALOG_DATA, useValue: config.data },
        { provide: DialogRef, useValue: dialogRef },
      ],
    });

    this.stack.update(entries => [
      ...entries,
      {
        id,
        component: component as Type<unknown>,
        injector,
        dialogRef: dialogRef as DialogRef<unknown>,
        disableClose: config.disableClose ?? false,
        width: config.width,
        maximizable: config.maximizable ?? false,
        isMaximized: signal((config.maximizable ?? false) && (config.startMaximized ?? false)),
      },
    ]);

    return dialogRef;
  }

  private _lockContentScroll(): void {
    const content = document.querySelector<HTMLElement>('.shell-content');
    if (!content) return;
    this.scrollLockOriginalTop = content.scrollTop;
    content.style.overflowY = 'hidden';
    content.scrollTop = 0;
  }

  private _unlockContentScroll(): void {
    const content = document.querySelector<HTMLElement>('.shell-content');
    if (!content) return;
    content.style.overflowY = '';
    content.scrollTop = this.scrollLockOriginalTop;
  }
}
