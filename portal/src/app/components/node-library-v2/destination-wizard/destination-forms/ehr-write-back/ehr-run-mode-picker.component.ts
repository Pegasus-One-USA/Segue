import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { EhrRunMode, EhrWriteVendor, WritableTarget, runModesFor, vendorLabel } from './ehr-write-back.model';

/**
 * The EHR Write-Back destination's Run mode: Live, Dry run, or (for Epic, eClinicalWorks and athenahealth) Test on a
 * FHIR server. Below it, what the chosen mode will actually do over the chosen connection: a test run reaches only
 * the test server, and a vendor whose write APIs are not activated (or that takes dry runs only) still only checks.
 *
 * Dry run is offered only while the EhrWriteBack:DryRunEnabled system setting is on (`dryRunOffered`), or for a node
 * saved as a dry run, which stays one until the user picks another mode (`dryRunSettingOff` explains why it shows).
 */
@Component({
  selector: 'app-ehr-run-mode-picker',
  standalone: true,
  imports: [ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['../../destination-wizard.component.scss'],
  styles: [':host { display: block; }'],
  template: `
    <div class="dw-field dw-field--full" role="radiogroup" aria-labelledby="dw-ewb-runmode-label">
      <span class="dw-label" id="dw-ewb-runmode-label">Run mode</span>
      @for (mode of modes(); track mode) {
        <label class="dw-label">
          <input type="radio" name="dw-ewb-runmode" [formControl]="control()" [value]="mode" [attr.data-mode]="mode" />
          {{ labelFor(mode) }}
        </label>
      }
      @if (!vendor()) {
        <span class="dw-hint">Pick a connection to see every run mode.</span>
      }
      @if (dryRunSettingOff() && runMode() === 'dryRun') {
        <span class="dw-hint" data-testid="ewb-dry-run-off-note">Dry run is turned off in System Settings. This
          destination stays a dry run until you choose another Run mode.</span>
      }
      @if (modes().includes('dryRun')) {
        <span class="dw-hint">Start with a dry run. Switch to Live only after a dry run of this workflow looks right.</span>
      } @else if (modes().includes('test')) {
        <span class="dw-hint">Start with a test on a FHIR server. Switch to Live only after a test run of this workflow
          looks right.</span>
      }
      @if (!runMode()) {
        <span class="dw-hint" data-testid="ewb-run-mode-required">Choose a Run mode.</span>
      }
      @if ((runMode() === 'live' || runMode() === 'test') && target(); as t) {
        @if (runMode() === 'test') {
          <span class="dw-hint dw-hint--warn">Test run: the types you select under Resource types are written to
            {{ connectionName() }}, shaped exactly as {{ vendorName() }} would receive them. Nothing reaches
            {{ vendorName() }}.</span>
        } @else if (t.liveTypes.length > 0) {
          <span class="dw-hint dw-hint--warn">Live: the types you select under Resource types will be written into
            {{ t.name }}.</span>
        } @else if (t.awaitingActivationTypes.length > 0) {
          <span class="dw-hint dw-hint--warn">{{ t.name }} does not have the {{ label(t.vendor) }} write APIs marked as
            activated, so this still runs as a dry run. Turn on "Vendor write APIs activated" on the connection once
            the practice has them.</span>
        } @else {
          <span class="dw-hint dw-hint--warn">{{ label(t.vendor) }} accepts dry runs only for now, so this still runs
            as a dry run.</span>
        }
      }
    </div>
  `,
})
export class EhrRunModePickerComponent {
  readonly control = input.required<FormControl<EhrRunMode | null>>();
  /** The vendor written to (or stood in for); null while not known. */
  readonly vendor = input<EhrWriteVendor | null>(null);
  /** The chosen run mode; null while none is (only Live offered, so it must be picked on purpose). */
  readonly runMode = input<EhrRunMode | null>('dryRun');
  /** Dry run is among the modes: the setting is on, or the node was saved as a dry run. */
  readonly dryRunOffered = input<boolean>(true);
  /** The EhrWriteBack:DryRunEnabled setting is off (Dry run shows only for a node saved as one). */
  readonly dryRunSettingOff = input<boolean>(false);
  /** What the destination writes as: the tested vendor in a test run, else the chosen connection. */
  readonly target = input<WritableTarget | null>(null);
  readonly connectionName = input<string | null>(null);

  readonly modes = computed(() => runModesFor(this.vendor(), this.dryRunOffered()));
  readonly vendorName = computed(() => vendorLabel(this.vendor()) || 'the EHR');

  labelFor(mode: EhrRunMode): string {
    if (mode === 'live') return `Live: write into ${this.vendorName()}`;
    if (mode === 'test') return `Test on a FHIR server: send exactly what ${this.vendorName()} would get to a test server instead`;
    return 'Dry run: check every record, send nothing';
  }

  label(vendor: string | null | undefined): string {
    return vendorLabel(vendor);
  }
}
