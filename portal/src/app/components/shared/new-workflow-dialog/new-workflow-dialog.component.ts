import { Component, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { WorkflowApiService } from '../../../services/workflow-api.service';
import { ToastService } from '../../../services/toast.service';

/**
 * "New workflow" — name and description first, then the builder. Shared by the Workflows list and the
 * Dashboard's New button, which used to link straight to an empty, unsaved builder.
 *
 * The workflow is created BEFORE the canvas opens, so it always has a real id. That is what removes the
 * "authored before the workflow existed" class of bug at its root: node config, mapping and transform rules
 * no longer have to be parked somewhere without an owner and reconciled on a later save.
 *
 * The host decides when it is shown (`@if`) and hides it again on `closed`. Permission to create is the host's
 * to check, as both hosts already do for their own New button.
 */
@Component({
  selector: 'app-new-workflow-dialog',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './new-workflow-dialog.component.html',
  styleUrls: ['./new-workflow-dialog.component.scss'],
})
export class NewWorkflowDialogComponent {
  private readonly api = inject(WorkflowApiService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  /** Emitted when the dialog is done — cancelled, discarded, or the workflow was created and the builder is
   *  opening. */
  readonly closed = output<void>();

  readonly name = signal('');
  readonly description = signal('');
  readonly creating = signal(false);
  /** Shows the "discard your input?" confirm layered over this dialog. */
  readonly confirmDiscard = signal(false);

  /** True once anything has been typed into the form. */
  private hasInput(): boolean {
    return !!this.name().trim() || !!this.description().trim();
  }

  /** Close attempt from ×, Cancel or Escape. Anything typed is confirmed before it is thrown away;
   *  an untouched form closes immediately, with nothing to lose. */
  cancel(): void {
    // Mid-create, and while the discard confirm is already up, the dialog ignores close attempts —
    // the confirm owns the interaction until it is answered.
    if (this.creating() || this.confirmDiscard()) return;

    if (this.hasInput()) {
      this.confirmDiscard.set(true);
      return;
    }

    this.closed.emit();
  }

  /** Confirmed discard — drops the typed values and closes. */
  discard(): void {
    this.confirmDiscard.set(false);
    this.closed.emit();
  }

  /** "Keep editing" — dismisses the confirm and leaves the form exactly as it was. */
  keepEditing(): void {
    this.confirmDiscard.set(false);
  }

  /** Creates an empty workflow (no nodes, no edges) and opens the builder on it. It starts life as Draft —
   *  it has no destination yet — and is created enabled, so adding a destination is all it takes to be Ready. */
  create(): void {
    const name = this.name().trim();
    if (!name || this.creating()) return;

    const description = this.description().trim();
    this.creating.set(true);
    this.api
      .save({ name, description: description || null, isEnabled: true, nodes: [], edges: [] })
      .subscribe({
        next: created => {
          this.creating.set(false);
          this.closed.emit();
          // new=1 tells the builder this workflow has an id but an EMPTY graph, so it resets the canvas
          // instead of taking the edit path — see its ngOnInit. Without it the builder treats the id as
          // an existing workflow to load, and the stale canvas state breaks the `+` picker.
          this.router.navigate(['/workflow-builder-v2'], {
            queryParams: { id: created.id, new: '1' },
          });
        },
        error: err => {
          this.creating.set(false);
          this.toast.error(this.messageOf(err, 'Could not create the workflow.'));
        },
      });
  }

  private messageOf(err: unknown, fallback: string): string {
    const e = err as { error?: { title?: string; error_description?: string } | string; message?: string };
    const body = e?.error;
    if (body && typeof body === 'object') {
      return body.error_description || body.title || e?.message || fallback;
    }
    return e?.message || fallback;
  }
}
