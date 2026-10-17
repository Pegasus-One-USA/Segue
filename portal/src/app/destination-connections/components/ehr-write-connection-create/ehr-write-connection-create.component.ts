import { AfterViewInit, Component, computed, inject, input, output, signal, viewChild } from '@angular/core';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { SourceConnectionModel } from '../../../source-connections/models/source-connection.model';
import { ConnectionKindCard } from '../../../connections/connection-row.model';
import { EhrWriteVendor, WriteVendorCard, writeVendorCard } from '../../../connections/ehr-write-vendors';
import {
  creatableWriteVendorCards,
  toConnectionKindCard,
  writeConnectionCreateCodes,
} from '../../../connections/connection-permissions';
import {
  ConnectionKindCardGroup,
  ConnectionKindPickerComponent,
} from '../../../connections/components/connection-kind-picker/connection-kind-picker.component';
import { EhrWriteConnectionEditorComponent } from '../ehr-write-connection-editor/ehr-write-connection-editor.component';


/**
 * Creates one EHR write connection on the go — from an EHR Write-Back destination's connection picker, for example.
 * With a vendor preset it opens that vendor's form straight away (the shared vendor form in write purpose, or the
 * small FHIR server form); with none it first offers a card per vendor the role may create. `created` hands back the
 * saved connection; `cancelled` fires when the form closes without one. New needs ehrwriteback.create plus the
 * vendor's own `.create` (both); the caller decides whether to offer this at all.
 */
@Component({
  selector: 'app-ehr-write-connection-create',
  standalone: true,
  imports: [ConnectionKindPickerComponent, EhrWriteConnectionEditorComponent],
  templateUrl: './ehr-write-connection-create.component.html',
  styleUrl: './ehr-write-connection-create.component.scss',
})
export class EhrWriteConnectionCreateComponent implements AfterViewInit {
  private readonly permissions = inject(PermissionService);
  private readonly actionGuard = inject(PermissionActionGuard);

  /** The vendor to create for; null shows the vendor cards first. */
  readonly vendor = input<EhrWriteVendor | null>(null);

  readonly created = output<SourceConnectionModel>();
  readonly cancelled = output<void>();

  private readonly editor = viewChild.required(EhrWriteConnectionEditorComponent);

  /** The vendor cards are showing (no vendor preset, nothing chosen yet). */
  readonly choosing = signal(false);
  private done = false;

  readonly cardGroups = computed<ConnectionKindCardGroup[]>(() => [{
    heading: 'EHR',
    cards: this.allowedCards().map(toConnectionKindCard),
  }]);

  ngAfterViewInit(): void {
    const vendor = this.vendor();
    if (vendor) {
      this.start(vendor);
    } else {
      this.choosing.set(true);
    }
  }

  /** The vendors the role may create a write connection for. */
  allowedCards(): WriteVendorCard[] {
    return creatableWriteVendorCards(code => this.permissions.hasPermission(code));
  }

  onCardPicked(card: ConnectionKindCard): void {
    this.choosing.set(false);
    this.start(card.value as EhrWriteVendor);
  }

  onPickerClosed(): void {
    this.choosing.set(false);
    this.finish(null);
  }

  onSaved(model: SourceConnectionModel | null): void {
    this.finish(model);
  }

  onClosed(): void {
    this.finish(null);
  }

  private start(vendor: EhrWriteVendor): void {
    if (!this.actionGuard.ensure(
      writeConnectionCreateCodes(vendor),
      `You do not have permission to add a new ${writeVendorCard(vendor)?.label ?? vendor} write connection.`,
      'all',
    )) {
      this.finish(null);
      return;
    }
    this.editor().openNew(vendor);
  }

  /** Reports once: the saved connection, or a cancel. */
  private finish(model: SourceConnectionModel | null): void {
    if (this.done) return;
    this.done = true;
    if (model) {
      this.created.emit(model);
    } else {
      this.cancelled.emit();
    }
  }
}
