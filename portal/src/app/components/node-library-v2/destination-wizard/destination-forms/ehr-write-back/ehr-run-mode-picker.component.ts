import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { EhrRunMode, EhrWriteVendor, WritableTarget, runModesFor, vendorLabel } from './ehr-write-back.model';

/**
 * The EHR Write-Back destination's Run mode: Live, Dry run, or (for Epic, eClinicalWorks and athenahealth) Test on a
 * FHIR server. Below it, what the chosen mode will actually do over the chosen connection: a test run reaches only
 * the test server, and a vendor whose write APIs are not activated (or that takes dry runs only) still only checks.
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
      <span class="dw-hint">Start with a dry run. Switch to Live only after a dry run of this workflow looks right.</span>
      @if (runMode() !== 'dryRun' && target(); as t) {
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
  readonly runMode = input<EhrRunMode>('dryRun');
  /** What the destination writes as: the tested vendor in a test run, else the chosen connection. */
  readonly target = input<WritableTarget | null>(null);
  readonly connectionName = input<string | null>(null);

  readonly modes = computed(() => runModesFor(this.vendor()));
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
