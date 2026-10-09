import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { PermissionService } from '../../../../../auth/services/permission.service';
import { SourceConnectionModel } from '../../../../../source-connections/models/source-connection.model';
import { creatableWriteVendorCards } from '../../../../../connections/connection-permissions';
import { EhrWriteConnectionCreateComponent } from '../../../../../destination-connections/components/ehr-write-connection-create/ehr-write-connection-create.component';
import { EhrRunMode, EhrWriteVendor, WritableTarget, vendorLabel } from './ehr-write-back.model';

/** Why the write connections could not be listed. */
export type EhrWriteTargetsError = 'noViewPermission' | 'failed';

/**
 * The write connection an EHR Write-Back destination writes through ("Write to"), or in a test run the FHIR test
 * server that stands in for the EHR ("Test server"). Only the connections that fit the run mode are passed in. New
 * connection creates one on the go (the shared create component); it is offered only to a role that may list write
 * connections here (sourceconnections.view, the right this picker's own list needs, so the new one can be picked) and
 * create one for this vendor (creatableWriteVendorCards: ehrwriteback.create plus the vendor's own .create).
 */
@Component({
  selector: 'app-ehr-write-connection-picker',
  standalone: true,
  imports: [ReactiveFormsModule, EhrWriteConnectionCreateComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['../../destination-wizard.component.scss'],
  styles: [':host { display: block; }'],
  template: `
    <div class="dw-field" [class.dw-field--error]="control().invalid && control().touched">
      <label class="dw-label" for="dw-ewb-target">{{ runMode() === 'test' ? 'Test server' : 'Write to' }} <span class="dw-req">*</span></label>
      <div class="dw-existing-connection-row">
        <div class="dw-select-wrap">
          <select id="dw-ewb-target" class="dw-select" [formControl]="control()">
            <option value="" disabled>{{ loading() ? 'Loading connections…' : 'Choose a connection' }}</option>
            @for (target of targets(); track target.id) {
              <option [value]="target.id">{{ target.name }} ({{ label(target.vendor) }})</option>
            }
          </select>
        </div>
        @if (canCreate()) {
          <button type="button" class="dw-btn" data-testid="ewb-new-connection" (click)="creating.set(true)">New connection</button>
        }
      </div>
      @if (control().invalid && control().touched) {
        <span class="dw-error">Choose where to write.</span>
      }
      @if (loadError() === 'noViewPermission') {
        <span class="dw-hint dw-hint--warn">You need permission to view Source Connections to choose a write
          connection. Ask an administrator.</span>
      } @else if (loadError() === 'failed') {
        <span class="dw-hint dw-hint--warn">Loading write connections failed. Try again.</span>
      } @else if (!loading() && targets().length === 0) {
        <span class="dw-hint">{{ emptyText() }}</span>
      }
    </div>

    @if (creating()) {
      <app-ehr-write-connection-create [vendor]="createVendor()" (created)="onCreated($event)" (cancelled)="creating.set(false)" />
    }
  `,
})
export class EhrWriteConnectionPickerComponent {
  private readonly permissions = inject(PermissionService);

  readonly control = input.required<FormControl<string | null>>();
  /** The connections that fit the vendor and run mode. */
  readonly targets = input<WritableTarget[]>([]);
  readonly loading = input<boolean>(false);
  readonly loadError = input<EhrWriteTargetsError | null>(null);
  readonly vendor = input<EhrWriteVendor | null>(null);
  readonly runMode = input<EhrRunMode>('dryRun');

  /** A connection was created here; the host reloads and picks it. */
  readonly created = output<SourceConnectionModel>();

  readonly creating = signal(false);

  /** A test run needs a FHIR server; otherwise the vendor's own (null: the create component asks which). */
  readonly createVendor = computed<EhrWriteVendor | null>(() => (this.runMode() === 'test' ? 'GenericFhir' : this.vendor()));

  readonly emptyText = computed(() => {
    const vendor = vendorLabel(this.vendor());
    if (this.runMode() === 'test') {
      return `No FHIR test server that can stand in for ${vendor} yet. Click New connection, or add one under Destination Connections.`;
    }
    return `No ${vendor || 'EHR'} write connection yet. Click New connection, or add one under Destination Connections.`;
  });

  /** Listing the new row here needs sourceconnections.view; creating it is creatableWriteVendorCards' rule. */
  canCreate(): boolean {
    if (this.loadError() === 'noViewPermission' || !this.permissions.hasPermission('sourceconnections.view')) return false;
    return creatableWriteVendorCards(code => this.permissions.hasPermission(code), this.createVendor()).length > 0;
  }

  label(vendor: string): string {
    return vendorLabel(vendor);
  }

  onCreated(connection: SourceConnectionModel): void {
    this.creating.set(false);
    this.created.emit(connection);
  }
}
