import { Component, DestroyRef, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { forkJoin, map, of, switchMap } from 'rxjs';
import { buildConnectionMetadata } from '../../../../destination-connections/utils/destination-connection-secret.util';
import { ISourceConnectionService } from '../../../../source-connections/services/i-source-connection.service';
import { SourceConnectionModel } from '../../../../source-connections/models/source-connection.model';
import { EhrWriteCapabilitiesService } from '../../../../services/ehr-write-capabilities.service';
import { EhrWriteBackFormApi } from './destination-form-api';

interface WritableTarget {
  id: string;
  name: string;
  vendor: string;
  resourceTypes: string[];
}

/**
 * EHR Write-Back destination form. Picks the EHR connection to write INTO — only enabled connections whose Access
 * includes Write and whose vendor accepts writes are offered — plus the write options. There is no secret: the
 * destination writes over the chosen connection's own credentials.
 *
 * Phase 1 runs every write as a dry run, whatever is chosen here, so the dry-run box is shown checked and locked.
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
              Read &amp; Write (Epic Backend System only).</span>
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
            left for review.</span>
        </div>

        <div class="dw-field dw-field--full">
          <label class="dw-label">
            <input type="checkbox" formControlName="dryRun" />
            Dry run — check and resolve every record, send nothing
          </label>
          <span class="dw-hint">Write-back is in its first phase: every run is a dry run. The run reports what it would
            have written.</span>
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
    // Locked on for Phase 1 — see the class remarks.
    dryRun: [{ value: true, disabled: true }],
  });

  constructor() {
    this.sourceConnections.getAll().pipe(
      map(connections => connections.filter(c => c.isEnabled && (c.access === 'Write' || c.access === 'ReadWrite'))),
      switchMap(connections => connections.length === 0
        ? of([] as WritableTarget[])
        : forkJoin(connections.map(c => this.capabilities.writableResourceTypes(c.sourceSystemType).pipe(
            map(resourceTypes => this.toTarget(c, resourceTypes)))))),
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
      // Always "true" in Phase 1 (the control is locked); stored so the setting survives once it unlocks.
      dest_dryRun: 'true',
      dest_createPatientIfMissing: v.createPatientIfMissing ? 'true' : 'false',
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
      dryRun: true,
    });
    this.selectedId.set(null);
  }

  private toTarget(connection: SourceConnectionModel, resourceTypes: string[]): WritableTarget {
    return { id: connection.id, name: connection.name, vendor: connection.sourceSystemType, resourceTypes };
  }
}
