import { Component, computed, input, linkedSignal, output } from '@angular/core';
import { A11yModule } from '@angular/cdk/a11y';
import { ModalOverlayComponent } from '../../shared/modal-overlay/modal-overlay.component';
import { CanvasNode } from '../../../models/node-v2.model';
import { MPI_IDENTIFIERS } from '../../../data/mpi-identifiers.data';
import { mpiIdentifierIds, serializeMpiIdentifiers } from '../../../services/mpi-node.util';

/**
 * Picks the identifiers the MPI node matches patients on, from the 18 HIPAA Safe Harbor identifiers. Opened by
 * clicking the MPI node; the builder writes the result back onto that node (see WorkflowBuilderV2Component).
 */
@Component({
  selector: 'app-mpi-identifiers-dialog',
  standalone: true,
  imports: [A11yModule, ModalOverlayComponent],
  templateUrl: './mpi-identifiers-dialog.component.html',
  styleUrl: './mpi-identifiers-dialog.component.scss',
})
export class MpiIdentifiersDialogComponent {
  readonly open = input(false);
  /** The MPI node being edited — its saved selection seeds the checklist each time the dialog opens. */
  readonly node = input<CanvasNode | null>(null);
  /** Display names of the sources feeding the MPI, shown so it's clear whose records are being matched. */
  readonly sourceNames = input<string[]>([]);
  /** View-only: the checklist shows the saved selection but can't change it (see canMutate in the builder). */
  readonly readOnly = input(false);

  readonly closed = output<void>();
  /** The chosen identifiers, serialized for the node's mpiIdentifiers field (catalog order, comma-separated). */
  readonly saved  = output<string>();

  protected readonly identifiers = MPI_IDENTIFIERS;

  private readonly savedIds = computed(() => {
    const node = this.node();
    return this.open() && node ? mpiIdentifierIds(node) : [];
  });

  /** The working selection — reset to the node's saved one whenever the dialog opens or the node changes. */
  protected readonly draft = linkedSignal(() => new Set(this.savedIds()));

  protected readonly selectedCount = computed(() => this.draft().size);
  protected readonly allSelected = computed(() => this.draft().size === this.identifiers.length);
  protected readonly dirty = computed(() =>
    serializeMpiIdentifiers(this.draft()) !== serializeMpiIdentifiers(this.savedIds()));

  protected isSelected(id: string): boolean {
    return this.draft().has(id);
  }

  toggle(id: string, checked: boolean): void {
    if (this.readOnly()) return;
    this.draft.update(current => {
      const next = new Set(current);
      if (checked) next.add(id); else next.delete(id);
      return next;
    });
  }

  selectAll(): void {
    if (this.readOnly()) return;
    this.draft.set(new Set(this.identifiers.map(identifier => identifier.id)));
  }

  clearAll(): void {
    if (this.readOnly()) return;
    this.draft.set(new Set());
  }

  save(): void {
    if (this.readOnly() || this.selectedCount() === 0) return;
    this.saved.emit(serializeMpiIdentifiers(this.draft()));
  }
}
