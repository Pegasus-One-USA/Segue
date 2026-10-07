import { Component, DestroyRef, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { forkJoin, map, of, switchMap } from 'rxjs';
import { buildConnectionMetadata } from '../../../../destination-connections/utils/destination-connection-secret.util';
import { ISourceConnectionService } from '../../../../source-connections/services/i-source-connection.service';
import { SourceConnectionModel } from '../../../../source-connections/models/source-connection.model';
import { EhrWriteCapabilitiesService, EhrWriteCapability } from '../../../../services/ehr-write-capabilities.service';
import { EhrWriteBackFormApi } from './destination-form-api';

interface WritableTarget {
  id: string;
  name: string;
  vendor: string;
  resourceTypes: string[];
  /** Types a run that is not a dry run sends when selected: every type the code supports live for this vendor, over
   *  this connection (types needing vendor activation count only when the connection has it). */
  liveTypes: string[];
  /** Types that would be live once the connection's vendor write APIs are activated. */
  awaitingActivationTypes: string[];
  /** Some types are filed on an encounter the bridge creates (eCW medical/surgical history). */
  offersHolderEncounter: boolean;
  /** The QA-only clone-mode system setting is on, so clone mode may be offered. */
  cloneModeEnabled: boolean;
}

/**
 * EHR Write-Back destination form. Picks the EHR connection to write INTO — only enabled connections whose Access
 * includes Write and whose vendor accepts writes are offered — plus the write options. There is no secret: the
 * destination writes over the chosen connection's own credentials.
 *
 * Dry run is the default. Clearing it sends every selected type the vendor supports live. eClinicalWorks and
 * athenahealth write through contracted / proprietary APIs, so their types go live only once the connection says the
 * practice has them activated; until then they still only check and count. There is no installation-wide release:
 * who may clear Dry run and run the workflow is decided by the EHR Write-Back permissions.
 */
@Component({
  selector: 'app-ehr-write-back-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  styleUrls: ['../destination-wizard.component.scss'],
  template: `
    <form [formGroup]="form" class="dw-form" autocomplete="off">
      <div class="dw-grid-2">
        <div class="dw-field" [class.dw-field--error]="form.get('name')!.invalid && form.get('name')!.touched">
          <label class="dw-label" for="dw-ewb-name">Destination name <span class="dw-req">*</span></label>
          <input id="dw-ewb-name" class="dw-input" formControlName="name" placeholder="e.g. Epic write-back" />
          @if (form.get('name')!.invalid && form.get('name')!.touched) {
            <span class="dw-error">Destination name is required.</span>
          }
        </div>

        <div class="dw-field" [class.dw-field--error]="form.get('sourceConnectionId')!.invalid && form.get('sourceConnectionId')!.touched">
          <label class="dw-label" for="dw-ewb-target">EHR connection to write to <span class="dw-req">*</span></label>
          <div class="dw-select-wrap">
            <select id="dw-ewb-target" class="dw-select" formControlName="sourceConnectionId" (change)="onTargetChanged()">
              <option value="" disabled>{{ loading() ? 'Loading connections…' : 'Choose a connection' }}</option>
              @for (target of targets(); track target.id) {
                <option [value]="target.id">{{ target.name }} ({{ target.vendor }})</option>
              }
            </select>
          </div>
          @if (form.get('sourceConnectionId')!.invalid && form.get('sourceConnectionId')!.touched) {
            <span class="dw-error">Choose the EHR connection to write to.</span>
          }
          @if (!loading() && targets().length === 0) {
            <span class="dw-hint">No connection can take writes yet. Set a connection's Access to Write or
              Read &amp; Write (Epic, eClinicalWorks or athenahealth, Backend System only).</span>
          }
        </div>

        <div class="dw-field">
          <label class="dw-label" for="dw-ewb-max">Max writes per run</label>
          <input id="dw-ewb-max" class="dw-input" formControlName="maxWritesPerRun" inputmode="numeric" placeholder="500" />
          @if (form.get('maxWritesPerRun')!.invalid) {
            <span class="dw-error">A whole number from 1 to 10000.</span>
          }
        </div>

        <div class="dw-field">
          <label class="dw-label" for="dw-ewb-docstatus">Clinical notes are filed as</label>
          <div class="dw-select-wrap">
            <select id="dw-ewb-docstatus" class="dw-select" formControlName="noteDocStatus">
              <option value="preliminary">Preliminary (a clinician reviews and signs)</option>
              <option value="final">Final (signed under the integration user)</option>
            </select>
          </div>
        </div>

        <div class="dw-field dw-field--full">
          <label class="dw-label">
            <input type="checkbox" formControlName="createPatientIfMissing" />
            Create the patient when the EHR has no match
          </label>
          <span class="dw-hint">Only used when Patient.$match finds no one. An uncertain match is never written; it is
            left for review. A created patient's records are filed in the same run.</span>
        </div>

        @if (selected()?.vendor === 'Healow' || selected()?.vendor === 'Athenahealth') {
          <div class="dw-field" [class.dw-field--error]="form.get('targetProviderId')!.invalid">
            <label class="dw-label" for="dw-ewb-provider">{{ selected()?.vendor === 'Healow' ? 'Note author (eCW practitioner id)' : 'Provider id (athena)' }}</label>
            <input id="dw-ewb-provider" class="dw-input" formControlName="targetProviderId" placeholder="e.g. 71" />
            <span class="dw-hint">{{ selected()?.vendor === 'Healow'
              ? 'eClinicalWorks files a note only with an author. Without one, notes are rejected.'
              : 'Optional: the provider athena files lab results and notes under.' }}</span>
          </div>
        }

        @if (selected()?.vendor === 'Athenahealth') {
          <div class="dw-field" [class.dw-field--error]="form.get('targetDepartmentId')!.invalid">
            <label class="dw-label" for="dw-ewb-department">Department id (athena)</label>
            <input id="dw-ewb-department" class="dw-input" formControlName="targetDepartmentId" placeholder="e.g. 1" />
            <span class="dw-hint">Where new patients are registered. Leave empty to write each chart in the patient's
              own primary department (new patients are then rejected).</span>
          </div>
        }

        @if (selected()?.offersHolderEncounter) {
          <div class="dw-field dw-field--full">
            <label class="dw-label">
              <input type="checkbox" formControlName="createHolderEncounter" />
              File medical and surgical history on a new telephone encounter
            </label>
            <span class="dw-hint">eClinicalWorks takes history items only on an open telephone encounter. When on, one
              is created per patient per run, and only when a history item is actually sent. Off, history is skipped.</span>
          </div>
        }

        @if (selected()?.cloneModeEnabled) {
          <div class="dw-field dw-field--full">
            <label class="dw-label">
              <input type="checkbox" formControlName="cloneMode" />
              Clone mode (QA only) — write each patient as a new synthetic test patient
            </label>
            <span class="dw-hint">Every source patient is created as a clone: "Zztest" before the family name, a shifted
              birth date, a synthetic SSN and no phone or email. Records are filed against the clone, never the
              original. Select Patient as well; notes and vitals are skipped because a clone has no encounters.</span>
          </div>
        }

        <div class="dw-field dw-field--full">
          <label class="dw-label">
            <input type="checkbox" formControlName="dryRun" />
            Dry run — check and resolve every record, send nothing
          </label>
          <span class="dw-hint">A dry run reports what it would have written. Turn it off only after a dry run of
            this workflow looks right.</span>
          @if (!form.value.dryRun && selected(); as target) {
            @if (target.liveTypes.length > 0) {
              <span class="dw-hint dw-hint--warn">Live: the types you select in Data groups will be written into
                {{ target.name }}.</span>
            } @else if (target.awaitingActivationTypes.length > 0) {
              <span class="dw-hint dw-hint--warn">{{ target.name }} does not have the {{ target.vendor }} write APIs
                marked as activated, so this still runs as a dry run. Turn on "Vendor write APIs activated" on the
                connection once the practice has them.</span>
            } @else {
              <span class="dw-hint dw-hint--warn">{{ target.vendor }} accepts dry runs only for now, so this still
                runs as a dry run.</span>
            }
          }
        </div>
      </div>
    </form>

    @if (selected(); as target) {
      <div class="dw-callout dw-callout--info" style="margin-top: 12px;">
        {{ target.vendor }} accepts writes for: {{ target.resourceTypes.join(', ') }}.
      </div>
    }
  `,
})
export class EhrWriteBackDestinationFormComponent implements EhrWriteBackFormApi {
  readonly kind = 'ehrWriteBack' as const;

  private readonly fb = inject(FormBuilder);
  private readonly sourceConnections = inject(ISourceConnectionService);
  private readonly capabilities = inject(EhrWriteCapabilitiesService);
  private readonly destroyRef = inject(DestroyRef);

  /** Declared so the wizard's activeFormInputs() can pass them uniformly; this form has no secret to reuse. */
  readonly reusingExisting = input<boolean>(false);
  readonly existingDestinationId = input<string | null>(null);
  readonly destinationType = input<string | null>(null);
  readonly fabricLandingMode = input<string | null>(null);

  readonly loading = signal(true);
  readonly targets = signal<WritableTarget[]>([]);
  private readonly selectedId = signal<string | null>(null);

  readonly selected = computed(() => this.targets().find(t => t.id === this.selectedId()) ?? null);
  readonly targetVendor = computed(() => this.selected()?.vendor ?? null);
  readonly writableResourceTypes = computed(() => this.selected()?.resourceTypes ?? []);

  readonly form = this.fb.group({
    name: ['EHR write-back', [Validators.required]],
    sourceConnectionId: ['', [Validators.required]],
    maxWritesPerRun: ['500', [Validators.pattern(/^(?:[1-9]\d{0,3}|10000)$/)]],
    noteDocStatus: ['preliminary'],
    createPatientIfMissing: [false],
    cloneMode: [false],
    createHolderEncounter: [false],
    targetProviderId: ['', [Validators.pattern(/^\S{0,64}$/)]],
    targetDepartmentId: ['', [Validators.pattern(/^\S{0,64}$/)]],
    dryRun: [true],
  });

  constructor() {
    this.sourceConnections.getAll().pipe(
      map(connections => connections.filter(c => c.isEnabled && (c.access === 'Write' || c.access === 'ReadWrite'))),
      switchMap(connections => connections.length === 0
        ? of([] as WritableTarget[])
        : forkJoin(connections.map(c => this.capabilities.forVendor(c.sourceSystemType).pipe(
            map(result => this.toTarget(c, result.capabilities, result.cloneModeEnabled)))))),
      map(targets => targets.filter(t => t.resourceTypes.length > 0)),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: targets => {
        this.targets.set(targets);
        this.loading.set(false);
        this.selectedId.set(this.form.value.sourceConnectionId || null);
      },
      error: () => {
        this.targets.set([]);
        this.loading.set(false);
      },
    });
  }

  onTargetChanged(): void {
    this.selectedId.set(this.form.value.sourceConnectionId || null);
  }

  isValid(): boolean {
    return this.form.valid;
  }

  getRawValue(): Record<string, unknown> {
    return this.form.getRawValue();
  }

  getFullConfig(): Record<string, string> {
    const v = this.form.getRawValue();
    return {
      dest_name: v.name ?? '',
      dest_sourceConnectionId: v.sourceConnectionId ?? '',
      dest_ehrVendor: this.targetVendor() ?? '',
      // Anything but an explicit false is a dry run, here and in the executor.
      dest_dryRun: v.dryRun === false ? 'false' : 'true',
      dest_createPatientIfMissing: v.createPatientIfMissing ? 'true' : 'false',
      // Only ever true while the QA setting is on; the writer refuses clone mode otherwise.
      dest_cloneMode: v.cloneMode && this.selected()?.cloneModeEnabled ? 'true' : 'false',
      dest_maxWritesPerRun: v.maxWritesPerRun || '500',
      dest_noteDocStatus: v.noteDocStatus === 'final' ? 'final' : 'preliminary',
      dest_createHolderEncounter: v.createHolderEncounter && this.selected()?.offersHolderEncounter ? 'true' : 'false',
      dest_targetProviderId: (v.targetProviderId ?? '').trim(),
      dest_targetDepartmentId: (v.targetDepartmentId ?? '').trim(),
    };
  }

  getMetadata(): { fields: Record<string, string>; secret?: string | null } | null {
    if (!this.isValid()) return null;
    return {
      fields: JSON.parse(buildConnectionMetadata(this.getFullConfig(), 'ehrwriteback')) as Record<string, string>,
      secret: '',
    };
  }

  patchFrom(fields: Record<string, string>): void {
    this.form.patchValue({
      name: fields['dest_name'] || this.form.value.name || 'EHR write-back',
      sourceConnectionId: fields['dest_sourceConnectionId'] || '',
      maxWritesPerRun: fields['dest_maxWritesPerRun'] || '500',
      noteDocStatus: fields['dest_noteDocStatus'] === 'final' ? 'final' : 'preliminary',
      createPatientIfMissing: fields['dest_createPatientIfMissing'] === 'true',
      cloneMode: fields['dest_cloneMode'] === 'true',
      createHolderEncounter: fields['dest_createHolderEncounter'] === 'true',
      targetProviderId: fields['dest_targetProviderId'] ?? '',
      targetDepartmentId: fields['dest_targetDepartmentId'] ?? '',
      dryRun: fields['dest_dryRun'] !== 'false',
    });
    this.selectedId.set(fields['dest_sourceConnectionId'] || null);
  }

  reset(): void {
    this.form.reset({
      name: 'EHR write-back',
      sourceConnectionId: '',
      maxWritesPerRun: '500',
      noteDocStatus: 'preliminary',
      createPatientIfMissing: false,
      cloneMode: false,
      createHolderEncounter: false,
      targetProviderId: '',
      targetDepartmentId: '',
      dryRun: true,
    });
    this.selectedId.set(null);
  }

  private toTarget(connection: SourceConnectionModel, capabilities: EhrWriteCapability[], cloneModeEnabled: boolean): WritableTarget {
    const activated = connection.vendorWriteApisActivated === true;
    const distinct = (types: string[]) => [...new Set(types)];
    return {
      id: connection.id,
      name: connection.name,
      vendor: connection.sourceSystemType,
      resourceTypes: distinct(capabilities.map(c => c.resourceType)),
      liveTypes: distinct(capabilities
        .filter(c => c.liveWriteSupported && (!c.requiresVendorActivation || activated))
        .map(c => c.resourceType)),
      awaitingActivationTypes: distinct(capabilities
        .filter(c => c.liveWriteSupported && c.requiresVendorActivation && !activated)
        .map(c => c.resourceType)),
      offersHolderEncounter: capabilities.some(c => c.createsHolderEncounter === true),
      cloneModeEnabled,
    };
  }
}
