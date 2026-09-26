import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { buildConnectionMetadata } from '../../../../destination-connections/utils/destination-connection-secret.util';
import { WizardDestinationFormApi } from './destination-form-api';
import { DestinationSchemaService } from '../../../../services/destination-schema.service';

/**
 * MongoDB destination connection form — extracted from DestinationWizardComponent's inline mongoForm. This is
 * the first place Mongo's admin-dialog-equivalent form exists: DestinationConnectionFormComponent never
 * supported Mongo before this refactor.
 */
@Component({
  selector: 'app-mongo-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './mongo-destination-form.component.html',
  styleUrls: ['../destination-wizard.component.scss'],
})
export class MongoDestinationFormComponent implements WizardDestinationFormApi {
  private readonly fb = inject(FormBuilder);
  private readonly schemaSvc = inject(DestinationSchemaService);

  readonly probeState = signal<'idle' | 'testing' | 'ok' | 'error'>('idle');
  readonly probeError = signal<string | null>(null);
  /** Real collection names from the last successful Test Connection. This form no longer picks a
   *  collection itself — the mapping canvas does, per resource — so these exist purely to seed the wizard's
   *  mongoCollections() signal (see DestinationWizardComponent.next()), which backs the canvas's
   *  "+ Add a collection…" picker. */
  readonly collections = signal<string[]>([]);

  /** Discriminates this form from CSV/SFTP's own unrelated testConnection()/probeState pair — see
   *  isMongoForm() in destination-form-api.ts for why duck-typing on those alone is unsafe. */
  readonly kind = 'mongo' as const;

  readonly mongoForm = this.fb.group({
    name: ['MongoDB Production', [Validators.required]],
    // Single URI (database embedded, e.g. mongodb://user:pass@host:27017/dbname?authSource=admin) — matches
    // what MappedMongoDestinationWriter expects. Treated as a whole as a secret: there's no live probe to
    // validate a split server/database/credentials form against, so one opaque field is simplest and avoids a
    // redundant connection-string-assembly step this form would otherwise need.
    connectionString: ['', [Validators.required]],
    writeMode: ['upsert', []],
    // No `collection` control: which collection each resource writes to is chosen per resource on the mapping
    // canvas ("+ Add a collection…"), and the build already sends `target: null` for Mongo precisely so those
    // per-resource choices win (see workflow-build-assembler-v2.service.ts's Mongo branch) — a single
    // destination-level collection here had no say in where anything was actually written. No
    // `createIfNotExists` control either: a missing collection is now always created (see
    // MappedMongoDestinationWriter.EnsureCollectionExistsAsync), which is also MongoDB's own native behaviour
    // on first write.
  });

  /** True while the host is reusing a previously-saved connection unchanged — connectionString is a secret that
   *  is never repopulated when patching from an existing connection, so requiring it here would permanently
   *  block reuse unless the user retypes it just to satisfy validation. */
  readonly reusingExisting = input<boolean>(false);
  /** The already-saved destination's id when reusing it unchanged — lets Test Connection resolve the stored
   *  connection string server-side instead of requiring it retyped (see DestinationSchemaService.testMongo). */
  readonly existingDestinationId = input<string | null>(null);

  constructor() {
    effect(() => {
      const requireConnectionString = !this.reusingExisting();
      untracked(() => {
        const ctrl = this.mongoForm.get('connectionString')!;
        ctrl.setValidators(requireConnectionString ? [Validators.required] : []);
        ctrl.updateValueAndValidity({ emitEvent: false });
      });
    });
  }

  isValid(): boolean {
    return this.mongoForm.valid;
  }

  /** The probe only needs the connection string (which embeds host/db/credentials); `name` isn't part of the
   *  connectivity check. Reusing an already-saved connection can test via its stored connection string
   *  instead, once a destinationId is known. */
  canTest(): boolean {
    return !!this.mongoForm.value.connectionString || (this.reusingExisting() && !!this.existingDestinationId());
  }

  /** Live connectivity check before saving: opens a Mongo client on the entered connection string and pings
   *  the database server-side (see MongoDestinationConnectionTestService), returning the database's real
   *  collection names on success. No `collection` is sent, so the service's optional collection-existence
   *  check never runs — this form no longer names one, and a collection that doesn't exist yet is created at
   *  write time rather than being an error. Never blocks Save. `onSettled` lets a caller (the wizard's "Next")
   *  advance immediately on success instead of needing a separate reactive watch, mirroring
   *  SqlFamilyFormApi.testConnection. */
  testConnection(onSettled?: (result: { connected: boolean }) => void): void {
    if (!this.canTest()) return;
    this.probeState.set('testing');
    this.probeError.set(null);
    const v = this.mongoForm.value;
    this.schemaSvc
      .testMongo({
        connectionString: v.connectionString ?? '',
        destinationId: this.existingDestinationId() ?? undefined,
      })
      .subscribe({
        next: res => {
          this.collections.set(res.collections ?? []);
          if (res.connected) {
            this.probeState.set('ok');
          } else {
            this.probeState.set('error');
            this.probeError.set(res.error ?? 'Connection failed.');
          }
          onSettled?.({ connected: res.connected });
        },
        error: err => {
          this.probeState.set('error');
          this.probeError.set(err?.error?.error ?? err?.error?.detail ?? err?.message ?? 'Connection failed.');
          onSettled?.({ connected: false });
        },
      });
  }

  getRawValue(): Record<string, unknown> {
    return this.mongoForm.getRawValue();
  }

  getFullConfig(): Record<string, string> {
    const v = this.mongoForm.value;
    return {
      dest_name: v.name ?? '',
      dest_connectionString: v.connectionString ?? '',
      dest_writeMode: v.writeMode ?? 'upsert',
    };
  }

  getMetadata(): { fields: Record<string, string>; secret?: string | null } | null {
    if (!this.isValid()) return null;
    const config = this.getFullConfig();
    return {
      fields: JSON.parse(buildConnectionMetadata(config, 'csv')) as Record<string, string>,
      secret: config['dest_connectionString'] || '',
    };
  }

  /** `target` (the DestinationConfigurationDto's own target column) is deliberately not accepted: Mongo's
   *  target used to be the destination-level collection, and there is no longer a control to patch it
   *  onto. Omitting the optional parameter still satisfies WizardDestinationFormApi.patchFrom. */
  patchFrom(fields: Record<string, string>): void {
    this.mongoForm.patchValue({
      name: fields['dest_name'] || this.mongoForm.value.name || 'MongoDB Production',
      // Never repopulated — a secret, and never returned by the API (matches selectExisting()/
      // _populateFromNode()'s original mongo branches, both of which always left this blank too).
      connectionString: '',
      writeMode: fields['dest_writeMode'] || 'upsert',
    });
  }

  reset(): void {
    this.mongoForm.reset({
      name: 'MongoDB Production',
      connectionString: '',
      writeMode: 'upsert',
    });
    this.collections.set([]);
    this.probeState.set('idle');
    this.probeError.set(null);
  }
}
