import { Component, Type, computed, effect, inject, input, signal, untracked, viewChild, afterNextRender, Injector } from '@angular/core';
import { NgComponentOutlet } from '@angular/common';
import { FormBuilder, Validators, ReactiveFormsModule } from '@angular/forms';
import { DestinationTypeV2 as DestinationType } from '../../../models/destination-configuration-v2.model';
import { DestinationConfigFormComponent } from '../../shared-v2/config-form/config-form-v2.contract';
import { DESTINATION_FORM_REGISTRY } from './destination-forms/destination-form.registry';
import { WizardDestinationFormApi } from './destination-forms/destination-form-api';
import { DestinationSchemaService } from '../../../services/destination-schema.service';
import { buildConnectionMetadata, buildFhirSecretBlob } from '../../../destination-connections/utils/destination-connection-secret.util';

export type DestinationConnectionFormMode = 'create' | 'edit' | 'view';

/**
 * Hosts whichever DESTINATION_FORM_REGISTRY component matches `destType` — the standalone Destination
 * Connections admin screen's equivalent of DestinationWizardComponent's Step 1. Used to take a narrow
 * `destType: 'sql' | 'csv'` and duplicate the sql/csv form-building logic inline; now it's a thin
 * NgComponentOutlet host, same as the wizard, so both surfaces render the identical per-type component
 * instead of two hand-kept-in-sync copies. `destType` is now the real DestinationType (not the old lossy
 * 'sql'/'csv' collapse) — see destination-connection-dialog.component.ts's toFormType() callers, which
 * resolve the concrete type (SqlServer/MySql/PostgreSql/AzureSql/Csv/Sftp/Mongo/...) before passing it down,
 * now that the in-form "Database engine" dropdown is gone (engine is fixed by which registry component loads,
 * not chosen inside the form — see SqlFamilyDestinationFormComponent).
 *
 * FhirRepository is the one exception: DESTINATION_FORM_REGISTRY's FhirRepository entry is only a minimal
 * placeholder stub (the registry predates the real Aidbox/FHIR feature), so it is deliberately NOT routed
 * through the dynamic outlet. Its rich form (auth-type-conditional fields, live Test Connection with SMART
 * discovery) stays hand-rolled here instead, field-for-field identical to DestinationWizardComponent's own
 * fhirForm, so the already-shipped, live-verified canvas wizard is never touched.
 */
@Component({
  selector: 'app-destination-connection-form',
  standalone: true,
  imports: [NgComponentOutlet, ReactiveFormsModule],
  templateUrl: './destination-connection-form.component.html',
  styleUrls: ['./destination-wizard.component.scss', './destination-connection-form.component.scss'],
})
export class DestinationConnectionFormComponent {
  private readonly fb = inject(FormBuilder);
  private readonly schemaSvc = inject(DestinationSchemaService);

  readonly destType = input.required<DestinationType>();
  readonly mode = input<DestinationConnectionFormMode>('create');
  /** dest_* keyed config bag to pre-populate the loaded form with, if any (see patchFrom on each
   *  destination-forms/ component, or _populateFhirFromConfig below for the FHIR special case). */
  readonly initialConfig = input<Record<string, string> | null>(null);

  readonly isFhir = computed(() => this.destType() === 'FhirRepository');
  readonly isReadOnly = computed(() => this.mode() === 'view');
  readonly formType = computed<Type<DestinationConfigFormComponent> | null>(() => DESTINATION_FORM_REGISTRY[this.destType()] ?? null);

  // Field-for-field identical to DestinationWizardComponent's own fhirForm — duplicated rather than shared
  // so the already-shipped, live-verified canvas wizard is never touched (see this component's header comment).
  readonly fhirForm = this.fb.group({
    name:          ['Aidbox Production', [Validators.required]],
    baseUrl:       ['', [Validators.required]],
    project:       ['', []],
    authType:      ['oauth2', [Validators.required]],
    writeMode:     ['upsert', []],
    // ── OAuth2 Client Credentials fields (conditional on authType) ───────────
    tokenEndpoint: ['', []],
    clientId:      ['', []],
    clientSecret:  ['', []],
    // ── Basic auth fields (conditional) ──────────────────────────────────────
    username:      ['', []],
    password:      ['', []],
    // ── Bearer token field (conditional) ─────────────────────────────────────
    bearerToken:   ['', []],
  });

  readonly probeState = signal<'idle' | 'testing' | 'ok' | 'error'>('idle');
  readonly probeError = signal<string | null>(null);

  private readonly formOutlet = viewChild(NgComponentOutlet);
  private readonly _pendingPatch = signal<Record<string, string> | null>(null);
  private readonly injector = inject(Injector);

  constructor() {
    this._syncFhirAuthValidators(this.fhirForm.controls.authType.value);
    this.fhirForm.controls.authType.valueChanges.subscribe(v => this._syncFhirAuthValidators(v));

    effect(() => {
      const config = this.initialConfig();
      untracked(() => {
        if (!config) return;
        if (this.isFhir()) {
          this._populateFhirFromConfig(config);
        } else {
          this._pendingPatch.set(config);
        }
      });
    });
    // Flushes a queued initialConfig patch the moment the loaded form component actually exists — mirrors
    // DestinationWizardComponent's identical pattern (ngOnInit-style population can run before the outlet's
    // child does). Only ever set for a non-FHIR destType — see the effect above.
    effect(() => {
      const outlet = this.formOutlet();
      const pending = this._pendingPatch();
      if (!outlet || !pending) return;
      // Deferred via afterNextRender() even on this first attempt, not just the retries inside
      // _flushPendingPatch — applying the patch synchronously here (mid-render, since this effect fires as
      // part of the newly-created component's own initial change-detection pass) flips the form from
      // invalid to valid *during* that same pass, which trips NG0100
      // (ExpressionChangedAfterItHasBeenCheckedError) on whatever "disabled" binding reads isValid(). Running
      // after the render is done avoids fighting Angular's own dev-mode consistency check.
      untracked(() => afterNextRender(() => this._flushPendingPatch(pending), { injector: this.injector }));
    });

    effect(() => {
      const readOnly = this.isReadOnly();
      untracked(() => {
        // Only the hand-rolled FHIR form needs to be told directly — every DESTINATION_FORM_REGISTRY
        // component manages its own read-only presentation (see [class.dcf-readonly] in the template).
        if (!this.isFhir()) return;
        if (readOnly) {
          this.fhirForm.disable({ emitEvent: false });
        } else {
          this.fhirForm.enable({ emitEvent: false });
          this._syncFhirAuthValidators(this.fhirForm.controls.authType.value);
        }
      });
    });
  }

  /** NgComponentOutlet's directive instance can exist (formOutlet() truthy) before it has actually
   *  instantiated its dynamic child — outlet.componentInstance is null with nothing thrown at all in that
   *  case, and the SQL-family wrappers' further nested viewChild can also throw once patchFrom() is called
   *  too early (see DestinationWizardComponent's identical _flushPendingFormPatch). Either way this is a
   *  silent, permanent drop unless retried: neither formOutlet() nor _pendingPatch() change again on their
   *  own once we get here, so re-reading formOutlet() fresh via afterNextRender() is what actually closes
   *  the gap, for both the "still null" and "threw" cases. */
  private _flushPendingPatch(pending: Record<string, string>): void {
    const form = this.formOutlet()?.componentInstance as WizardDestinationFormApi | null;
    if (!form) {
      afterNextRender(() => this._flushPendingPatch(pending), { injector: this.injector });
      return;
    }
    try {
      form.patchFrom(pending);
      this._pendingPatch.set(null);
    } catch (err) {
      console.error('Destination form was not ready to restore its saved values — retrying after next render.', err);
      afterNextRender(() => this._flushPendingPatch(pending), { injector: this.injector });
    }
  }

  private activeForm(): WizardDestinationFormApi | null {
    return (this.formOutlet()?.componentInstance as WizardDestinationFormApi | null) ?? null;
  }

  isValid(): boolean {
    return this.isFhir() ? this.fhirForm.valid : (this.activeForm()?.isValid() ?? false);
  }

  /** FHIR credentials aren't validated server-side the way SQL's are checked against a real schema fetch
   *  before Save is even possible — a wrong secret would otherwise save silently and only fail later at run
   *  time. Every other destination type now owns its own Test Connection UI/gating inside its
   *  destination-forms/ component (see e.g. SftpDestinationFormComponent), so this host only ever needs to
   *  gate FHIR's hand-rolled one. */
  canTestConnection(): boolean {
    return this.isFhir() && !this.isReadOnly();
  }

  testConnection(): void {
    if (this.isFhir()) this._testFhir();
  }

  /** The non-secret fields + assembled secret for the current form, or null if invalid. For every
   *  DESTINATION_FORM_REGISTRY-routed type this delegates straight to the loaded component (see e.g.
   *  SqlFamilyDestinationFormComponent.getMetadata()); FHIR builds it inline via the same
   *  buildConnectionMetadata/buildFhirSecretBlob utilities every other component's getMetadata() uses. */
  getMetadata(): { fields: Record<string, string>; secret?: string | null } | null {
    if (this.isFhir()) {
      if (!this.fhirForm.valid) return null;
      const config = this._buildFhirConfig();
      return {
        fields: JSON.parse(buildConnectionMetadata(config, 'fhir')) as Record<string, string>,
        secret: buildFhirSecretBlob(config),
      };
    }
    return this.activeForm()?.getMetadata() ?? null;
  }

  private _buildFhirConfig(): Record<string, string> {
    const v = this.fhirForm.getRawValue();
    const config: Record<string, string> = {};
    config['dest_name'] = v.name ?? '';
    config['dest_baseUrl'] = v.baseUrl ?? '';
    config['dest_project'] = v.project ?? '';
    config['dest_authType'] = v.authType ?? 'oauth2';
    // Mirrors DestinationWizardComponent._save()'s fhir branch exactly: "Bundle"/"Transaction" write modes are
    // really two orthogonal backend fields folded into one dropdown (see that component's own doc comment).
    const writeMode = v.writeMode ?? 'upsert';
    config['dest_writeMode'] = writeMode === 'upsertBundle' || writeMode === 'upsertTransaction' ? 'upsert' : writeMode;
    config['dest_fhirWriteMode'] = writeMode === 'upsertBundle' ? 'bundle' : writeMode === 'upsertTransaction' ? 'transaction' : 'individual';
    if (v.authType === 'oauth2') {
      config['dest_tokenEndpoint'] = v.tokenEndpoint ?? '';
      config['dest_clientId'] = v.clientId ?? '';
      config['dest_clientSecret'] = v.clientSecret ?? '';
    } else if (v.authType === 'basic') {
      config['dest_username'] = v.username ?? '';
      config['dest_password'] = v.password ?? '';
    } else if (v.authType === 'bearer') {
      config['dest_bearerToken'] = v.bearerToken ?? '';
    }
    return config;
  }

  // No tokenEndpoint in the request — for oauth2/clientCredentials the backend discovers it from baseUrl via
  // GET {baseUrl}/.well-known/smart-configuration and returns it as resolvedTokenEndpoint, patched into this
  // form's (now-hidden) tokenEndpoint control on success so it still lands in dest_tokenEndpoint at save time.
  private _testFhir(): void {
    const v = this.fhirForm.value;
    this.probeState.set('testing');
    this.probeError.set(null);
    this.schemaSvc
      .testFhir({
        baseUrl: v.baseUrl ?? '',
        authType: v.authType ?? 'oauth2',
        clientId: v.clientId ?? undefined,
        clientSecret: v.clientSecret ?? undefined,
        username: v.username ?? undefined,
        password: v.password ?? undefined,
        bearerToken: v.bearerToken ?? undefined,
      })
      .subscribe({
        next: res => {
          if (!res.connected) {
            this.probeState.set('error');
            this.probeError.set(res.error ?? 'Connection failed.');
            return;
          }
          if (res.resolvedTokenEndpoint) {
            this.fhirForm.patchValue({ tokenEndpoint: res.resolvedTokenEndpoint });
          }
          this.probeState.set('ok');
        },
        error: err => {
          this.probeState.set('error');
          this.probeError.set(typeof err?.error?.error === 'string' ? err.error.error : (err?.message ?? 'Connection failed.'));
        },
      });
  }

  private _populateFhirFromConfig(f: Record<string, string>): void {
    // 'conditional' is a stale value from the now-removed "Conditional update by identifier" option (never
    // implemented server-side) — remap it to 'upsert' so an old destination still shows a valid selection.
    const writeMode = f['dest_fhirWriteMode'] === 'bundle'
      ? 'upsertBundle'
      : f['dest_fhirWriteMode'] === 'transaction'
        ? 'upsertTransaction'
        : f['dest_writeMode'] === 'conditional'
          ? 'upsert'
          : f['dest_writeMode'] || 'upsert';
    this.fhirForm.patchValue({
      name: f['dest_name'] || '',
      baseUrl: f['dest_baseUrl'] || '',
      project: f['dest_project'] || '',
      authType: f['dest_authType'] || 'oauth2',
      writeMode,
      tokenEndpoint: f['dest_tokenEndpoint'] || '',
      clientId: f['dest_clientId'] || '',
      clientSecret: '',
      username: f['dest_username'] || '',
      password: '',
      bearerToken: '',
    });
    this._syncFhirAuthValidators(this.fhirForm.value.authType ?? null);
  }

  private _syncFhirAuthValidators(authType: string | null): void {
    // tokenEndpoint is deliberately NOT in this list — see _testFhir()'s comment.
    (['clientId', 'clientSecret'] as const).forEach(name => {
      const ctrl = this.fhirForm.get(name)!;
      ctrl.setValidators(authType === 'oauth2' ? [Validators.required] : []);
      ctrl.updateValueAndValidity({ emitEvent: false });
    });
    (['username', 'password'] as const).forEach(name => {
      const ctrl = this.fhirForm.get(name)!;
      ctrl.setValidators(authType === 'basic' ? [Validators.required] : []);
      ctrl.updateValueAndValidity({ emitEvent: false });
    });
    const bearerToken = this.fhirForm.get('bearerToken')!;
    bearerToken.setValidators(authType === 'bearer' ? [Validators.required] : []);
    bearerToken.updateValueAndValidity({ emitEvent: false });
  }
}
