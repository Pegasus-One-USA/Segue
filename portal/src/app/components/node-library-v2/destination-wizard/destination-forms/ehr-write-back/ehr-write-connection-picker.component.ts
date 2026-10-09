import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { PermissionService } from '../../../../../auth/services/permission.service';
import { SourceConnectionModel } from '../../../../../source-connections/models/source-connection.model';
import { creatableWriteVendorCards } from '../../../../../connections/connection-permissions';
import { EhrWriteConnectionCreateComponent } from '../../../../../destination-connections/components/ehr-write-connection-create/ehr-write-connection-create.component';
import { EhrWriteVendor, WritableTarget, isTestableVendor, vendorLabel } from './ehr-write-back.model';

/** Why the write connections could not be listed. */
export type EhrWriteTargetsError = 'noViewPermission' | 'failed';

/**
 * Step 1 of an EHR Write-Back destination: the one "Connection" dropdown. For an EHR tile it shows two groups, the
 * EHR's own write connections (a live run) and the FHIR test servers that receive exactly what the EHR would (a test
 * run); without a vendor it lists every write connection. Nothing is ever chosen for the user. New connection creates
 * one on the go (the shared create component, as on Destination Connections): for an EHR tile, either an EHR
 * connection or a test server. It is offered only to a role that may list write connections here
 * (sourceconnections.view, so the new one can be picked) and create one for that vendor (creatableWriteVendorCards).
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
      <label class="dw-label" for="dw-ewb-target">Connection <span class="dw-req">*</span></label>
      <div class="dw-existing-connection-row">
        <div class="dw-select-wrap">
          <select id="dw-ewb-target" class="dw-select" [formControl]="control()">
            <option value="" disabled>{{ loading() ? 'Loading connections…' : 'Choose a connection' }}</option>
            @if (grouped()) {
              @if (own().length > 0) {
                <optgroup [label]="label(vendor())" data-testid="ewb-group-own">
                  @for (target of own(); track target.id) {
                    <option [value]="target.id">{{ target.name }}</option>
                  }
                </optgroup>
              }
              @if (testServers().length > 0) {
                <optgroup [label]="testGroupLabel()" data-testid="ewb-group-test">
                  @for (target of testServers(); track target.id) {
                    <option [value]="target.id">{{ target.name }}</option>
                  }
                </optgroup>
              }
            } @else {
              @for (target of own(); track target.id) {
                <option [value]="target.id">{{ target.name }} ({{ label(target.vendor) }})</option>
              }
            }
          </select>
        </div>
        @for (choice of createChoices(); track choice.vendor) {
          <button type="button" class="dw-btn" [attr.data-testid]="choice.testId" (click)="creating.set(choice.vendor)">
            {{ choice.text }}
          </button>
        }
      </div>
      @if (control().invalid && control().touched) {
        <span class="dw-error">Choose a connection.</span>
      }
      @if (loadError() === 'noViewPermission') {
        <span class="dw-hint dw-hint--warn">You need permission to view Source Connections to choose a write
          connection. Ask an administrator.</span>
      } @else if (loadError() === 'failed') {
        <span class="dw-hint dw-hint--warn">Loading write connections failed. Try again.</span>
      } @else if (!loading() && own().length === 0 && testServers().length === 0) {
        <span class="dw-hint">{{ emptyText() }}</span>
      }
    </div>

    @if (creating(); as createVendor) {
      <app-ehr-write-connection-create [vendor]="createVendor === 'any' ? null : createVendor"
        (created)="onCreated($event)" (cancelled)="creating.set(null)" />
    }
  `,
})
export class EhrWriteConnectionPickerComponent {
  private readonly permissions = inject(PermissionService);

  readonly control = input.required<FormControl<string | null>>();
  /** The EHR the dropdown is grouped for; null lists `own` flat (every write connection). */
  readonly vendor = input<EhrWriteVendor | null>(null);
  /** The EHR's own write connections, or every write connection when there is no vendor. */
  readonly own = input<WritableTarget[]>([]);
  /** FHIR test servers that receive exactly what the EHR would. */
  readonly testServers = input<WritableTarget[]>([]);
  readonly loading = input<boolean>(false);
  readonly loadError = input<EhrWriteTargetsError | null>(null);

  /** A connection was created here; the host reloads it. */
  readonly created = output<SourceConnectionModel>();

  /** The vendor a connection is being created for ('any': the create component asks which). */
  readonly creating = signal<EhrWriteVendor | 'any' | null>(null);

  /** Two groups for an EHR; a plain list without one. */
  readonly grouped = computed(() => this.vendor() !== null);

  readonly testGroupLabel = computed(() => `Test servers (receive exactly what ${vendorLabel(this.vendor())} would)`);

  readonly emptyText = computed(() => {
    const vendor = vendorLabel(this.vendor());
    return `No ${vendor || 'EHR'} write connection or test server yet. Click New connection, or add one under `
      + 'Destination Connections.';
  });

  /** The New buttons this role may use: the EHR's own and a test server for an EHR tile, one plain button without. */
  readonly createChoices = computed<{ vendor: EhrWriteVendor | 'any'; text: string; testId: string }[]>(() => {
    if (this.loadError() === 'noViewPermission' || !this.permissions.hasPermission('sourceconnections.view')) return [];
    const may = (vendor: EhrWriteVendor | null) =>
      creatableWriteVendorCards(code => this.permissions.hasPermission(code), vendor).length > 0;
    const vendor = this.vendor();
    if (vendor === null) {
      return may(null) ? [{ vendor: 'any', text: 'New connection', testId: 'ewb-new-connection' }] : [];
    }
    const choices: { vendor: EhrWriteVendor | 'any'; text: string; testId: string }[] = [];
    if (may(vendor)) {
      choices.push({ vendor, text: `New ${vendorLabel(vendor)} connection`, testId: 'ewb-new-connection' });
    }
    if (isTestableVendor(vendor) && may('GenericFhir')) {
      choices.push({ vendor: 'GenericFhir', text: 'New test server', testId: 'ewb-new-test-server' });
    }
    return choices;
  });

  label(vendor: string | null): string {
    return vendorLabel(vendor);
  }

  onCreated(connection: SourceConnectionModel): void {
    this.creating.set(null);
    this.created.emit(connection);
  }
}
