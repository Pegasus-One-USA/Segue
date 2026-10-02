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
  /** Types a run that is not a dry run would actually send (released by an administrator). */
  liveTypes: string[];
  /** The QA-only clone-mode system setting is on, so clone mode may be offered. */
  cloneModeEnabled: boolean;
}

/**
 * EHR Write-Back destination form. Picks the EHR connection to write INTO — only enabled connections whose Access
 * includes Write and whose vendor accepts writes are offered — plus the write options. There is no secret: the
 * destination writes over the chosen connection's own credentials.
 *
 * Dry run is the default. Clearing it sends only the types an administrator released for live writes (system
 * setting EhrWriteBack:LiveWriteTypes); every other type is still only checked and counted, and the form says which.
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
              <span class="dw-hint dw-hint--warn">Live: {{ target.liveTypes.join(', ') }} will be written into
                {{ target.name }}. Other selected types stay a dry run.</span>
            } @else {
              <span class="dw-hint dw-hint--warn">No {{ target.vendor }} type is released for live writes yet, so this
                still runs as a dry run. An administrator releases types in System Settings
                (EhrWriteBack:LiveWriteTypes).</span>
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
      dryRun: true,
    });
    this.selectedId.set(null);
  }

  private toTarget(connection: SourceConnectionModel, capabilities: EhrWriteCapability[], cloneModeEnabled: boolean): WritableTarget {
    return {
      id: connection.id,
      name: connection.name,
      vendor: connection.sourceSystemType,
      resourceTypes: capabilities.map(c => c.resourceType),
      liveTypes: capabilities.filter(c => c.liveReleased).map(c => c.resourceType),
      cloneModeEnabled,
    };
  }
}
