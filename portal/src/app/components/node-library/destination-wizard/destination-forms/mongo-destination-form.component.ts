import { Component, inject, signal } from '@angular/core';
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
  /** Real collection names from the last successful Test Connection — feeds the Collection field's
   *  datalist so an existing collection can be picked instead of typed blind. */
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
    collection: ['', [Validators.required]],
    writeMode: ['upsert', []],
    // Off by default: the collection must already exist unless explicitly opted out of (matches the writer's
    // "customer owns the destination" default — see MappedMongoDestinationWriter.WriteAsync).
    createIfNotExists: [false, []],
  });

  isValid(): boolean {
    return this.mongoForm.valid;
  }

  /** The probe only needs the connection string (which embeds host/db/credentials); the other required fields
   *  (name, collection) aren't part of the connectivity check. */
  canTest(): boolean {
    return !!this.mongoForm.value.connectionString;
  }

  /** Live connectivity + collection-existence check before saving: opens a Mongo client on the entered
   *  connection string, pings the database, and (unless "Create collection if not exists" is checked) verifies
   *  the target collection actually exists — server-side, see MongoDestinationConnectionTestService. Never
   *  blocks Save. `onSettled` lets a caller (the wizard's "Next") advance immediately on success instead of
   *  needing a separate reactive watch, mirroring SqlFamilyFormApi.testConnection. */
  testConnection(onSettled?: (result: { connected: boolean }) => void): void {
    if (!this.canTest()) return;
    this.probeState.set('testing');
    this.probeError.set(null);
    const v = this.mongoForm.value;
    this.schemaSvc
      .testMongo({
        connectionString: v.connectionString ?? '',
        collection: v.collection || undefined,
        createIfNotExists: v.createIfNotExists ?? false,
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
      dest_collection: v.collection ?? '',
      dest_writeMode: v.writeMode ?? 'upsert',
      dest_createCollectionIfNotExists: String(v.createIfNotExists ?? false),
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

  patchFrom(fields: Record<string, string>, target?: string | null): void {
    this.mongoForm.patchValue({
      name: fields['dest_name'] || this.mongoForm.value.name || 'MongoDB Production',
      // Never repopulated — a secret, and never returned by the API (matches selectExisting()/
      // _populateFromNode()'s original mongo branches, both of which always left this blank too).
      connectionString: '',
      collection: fields['dest_collection'] || target || '',
      writeMode: fields['dest_writeMode'] || 'upsert',
      createIfNotExists: fields['dest_createCollectionIfNotExists'] === 'true',
    });
  }

  reset(): void {
    this.mongoForm.reset({
      name: 'MongoDB Production',
      connectionString: '',
      collection: '',
      writeMode: 'upsert',
      createIfNotExists: false,
    });
    this.collections.set([]);
    this.probeState.set('idle');
    this.probeError.set(null);
  }
}
