import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AbstractControl, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatIconModule } from '@angular/material/icon';
import { catchError, forkJoin, of } from 'rxjs';
import { CdsApiService } from '../../services/cds-api.service';
import {
  CdsDiscoveryInfo,
  CdsFixSettings,
  CdsHandler,
  CdsServiceInstance,
  CdsTrustedIssuer,
  SaveCdsServiceInstanceRequest,
} from '../../models/cds.models';
import { cdsHookLabel } from '../../models/cds-labels';
import { CdsSettingsGroup, buildCdsSettingsGroup, readCdsSettings } from '../../models/cds-settings.util';
import {
  CDS_DEFAULT_OVERRIDE_REASONS,
  CdsFixesGroup,
  CdsLinksArray,
  CdsReasonsArray,
  buildCdsFixesGroup,
  buildCdsLinksArray,
  buildCdsReasonsArray,
  cdsFixesInvalid,
  readCdsFixes,
  readCdsLinks,
  readCdsReasons,
} from '../../models/cds-service-options.util';
import { CDS_SERVICE_ID_PATTERN, absoluteHttpUrlValidator, toCdsServiceId } from '../../models/cds-validators';
import { CdsFixesConfirmService } from '../../services/cds-fixes-confirm.service';
import { CdsSettingsFormComponent } from '../../components/cds-settings-form/cds-settings-form.component';
import { CdsAllowedIssuersComponent } from '../../components/cds-allowed-issuers/cds-allowed-issuers.component';
import { CdsPrefetchSummaryComponent } from '../../components/cds-prefetch-summary/cds-prefetch-summary.component';
import { CdsCopyButtonComponent } from '../../components/cds-copy-button/cds-copy-button.component';
import { CdsFetchMissingDataComponent } from '../../components/cds-fetch-missing-data/cds-fetch-missing-data.component';
import { CdsFixesEditorComponent } from '../../components/cds-fixes-editor/cds-fixes-editor.component';
import { CdsOverrideReasonsEditorComponent } from '../../components/cds-override-reasons-editor/cds-override-reasons-editor.component';
import { CdsLinksEditorComponent } from '../../components/cds-links-editor/cds-links-editor.component';
import { CdsFixesBadgeComponent } from '../../components/cds-fixes-badge/cds-fixes-badge.component';
import { CdsSignOffComponent } from '../../components/cds-sign-off/cds-sign-off.component';
import { CdsCoverageServiceNoteComponent } from '../../components/cds-coverage-service-note/cds-coverage-service-note.component';
import { CDS_COVERAGE_HANDLER_KEY } from '../../models/cds-coverage.models';
import { HasUnsavedChanges } from '../../../core/guards/has-unsaved-changes';
import { ToastService } from '../../../services/toast.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';

type SetupControlName = 'handlerKey' | 'hook' | 'title' | 'serviceId' | 'description' | 'cardSourceLabel' | 'cardSourceUrl';

/**
 * Full-page setup for one CDS service (services/new and services/:id). The settings section is generated from
 * the chosen check's settings descriptor, so a new check type needs no change here.
 */
@Component({
  selector: 'app-cds-service-setup',
  standalone: true,
  imports: [
    DatePipe,
    RouterLink,
    ReactiveFormsModule,
    MatIconModule,
    CdsSettingsFormComponent,
    CdsAllowedIssuersComponent,
    CdsPrefetchSummaryComponent,
    CdsCopyButtonComponent,
    CdsFetchMissingDataComponent,
    CdsFixesEditorComponent,
    CdsOverrideReasonsEditorComponent,
    CdsLinksEditorComponent,
    CdsFixesBadgeComponent,
    CdsSignOffComponent,
    CdsCoverageServiceNoteComponent,
  ],
  templateUrl: './cds-service-setup.component.html',
  styleUrl: './cds-service-setup.component.scss',
})
export class CdsServiceSetupComponent implements OnInit, HasUnsavedChanges {
  private readonly route       = inject(ActivatedRoute);
  private readonly router      = inject(Router);
  private readonly api         = inject(CdsApiService);
  private readonly toast       = inject(ToastService);
  private readonly permissions = inject(PermissionService);
  private readonly actionGuard = inject(PermissionActionGuard);
  private readonly fixesConfirm = inject(CdsFixesConfirmService);
  private readonly destroyRef  = inject(DestroyRef);

  private readonly instanceId = this.route.snapshot.paramMap.get('id');
  readonly isNew = !this.instanceId;

  readonly loading      = signal(true);
  readonly loadError    = signal<string | null>(null);
  readonly saving       = signal(false);
  readonly submitted    = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly handlers  = signal<CdsHandler[]>([]);
  readonly issuers   = signal<CdsTrustedIssuer[]>([]);
  readonly instance  = signal<CdsServiceInstance | null>(null);
  readonly discovery = signal<CdsDiscoveryInfo | null>(null);

  /** Stays false until the user types in the service id, so the id keeps following the name on a new service. */
  private serviceIdEdited = false;

  readonly form = new FormGroup({
    handlerKey: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    hook: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    title: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(200)] }),
    serviceId: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.minLength(3), Validators.maxLength(100), Validators.pattern(CDS_SERVICE_ID_PATTERN)],
    }),
    description: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(2000)] }),
    isEnabled: new FormControl(true, { nonNullable: true }),
    cardSourceLabel: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(200)] }),
    cardSourceUrl: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(1000), absoluteHttpUrlValidator] }),
    allowedIssuerIds: new FormControl<string[]>([], { nonNullable: true }),
    fetchMissingData: new FormControl(true, { nonNullable: true }),
  });

  readonly settingsGroup = signal<CdsSettingsGroup>(buildCdsSettingsGroup([], null));
  readonly fixesGroup    = signal<CdsFixesGroup>(buildCdsFixesGroup([], null));
  readonly reasonsArray  = signal<CdsReasonsArray>(buildCdsReasonsArray(null));
  readonly linksArray    = signal<CdsLinksArray>(buildCdsLinksArray(null));

  private readonly handlerKeyValue = toSignal(this.form.controls.handlerKey.valueChanges, { initialValue: '' });
  private readonly hookValue       = toSignal(this.form.controls.hook.valueChanges, { initialValue: '' });
  private readonly serviceIdValue  = toSignal(this.form.controls.serviceId.valueChanges, { initialValue: '' });

  readonly handler = computed(() => this.handlers().find(h => h.key === this.handlerKeyValue()) ?? null);
  readonly prefetch = computed(() => this.handler()?.prefetchByHook[this.hookValue()] ?? {});
  /** The chosen check runs on the SAMPLE rules shipped with Segue (older APIs don't say: assume it does). */
  readonly usesSampleData = computed(() =>
    this.handler()?.usesSampleReferenceData ?? this.instance()?.usesSampleReferenceData ?? true);
  /** Coverage requirements (CRD): payers and rules live on the Coverage tab, and fixes do not apply. */
  readonly isCoverage = computed(() => this.handlerKeyValue() === CDS_COVERAGE_HANDLER_KEY);
  /** The fix types the chosen check offers (the fixes group is built from these, in this order). */
  readonly fixTypes = computed(() => this.handler()?.fixTypes ?? []);
  /** The standard dismiss reasons, as the API reports them for a saved service that uses them. */
  readonly defaultReasons = computed(() => {
    const instance = this.instance();
    return instance?.overrideReasonsAreDefault && instance.overrideReasons.length
      ? instance.overrideReasons
      : CDS_DEFAULT_OVERRIDE_REASONS;
  });
  readonly hookLabel = cdsHookLabel;

  /** The URL the EHR calls, previewed live from the id being typed. */
  readonly serviceUrl = computed(() => {
    const base = this.discovery()?.publicBaseUrl;
    const id = this.serviceIdValue().trim().toLowerCase();
    return base && id ? `${base}/cds-services/${id}` : null;
  });

  readonly canSave = computed(() =>
    this.permissions.hasPermission(this.isNew ? 'cdshooks.create' : 'cdshooks.edit'));

  /** Recording or removing a clinical sign-off is an edit of a saved service. */
  readonly canSignOff = computed(() => this.permissions.hasPermission('cdshooks.edit'));

  /** Set when an existing service names a check that is no longer available. */
  readonly handlerMissing = computed(() => !this.loading() && !!this.handlerKeyValue() && !this.handler());

  ngOnInit(): void {
    forkJoin({
      handlers: this.api.getHandlers(),
      issuers: this.api.getTrustedIssuers(),
      instance: this.instanceId ? this.api.getService(this.instanceId) : of(null),
      // Only used for the URL preview, so a failure here never blocks the page.
      discovery: this.api.getDiscoveryInfo().pipe(catchError(() => of(null))),
    }).subscribe({
      next: ({ handlers, issuers, instance, discovery }) => {
        this.handlers.set(handlers);
        this.issuers.set(issuers);
        this.discovery.set(discovery);
        if (instance) this.applyInstance(instance);
        else this.applyNew(this.route.snapshot.queryParamMap.get('handler'));
        if (this.isNew) {
          this.watchHandlerChanges();
          this.watchTitleChanges();
        }
        if (!this.canSave()) {
          this.form.disable({ emitEvent: false });
          this.settingsGroup().disable({ emitEvent: false });
          this.fixesGroup().disable({ emitEvent: false });
          this.reasonsArray().disable({ emitEvent: false });
          this.linksArray().disable({ emitEvent: false });
        }
        this.loading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        this.loading.set(false);
        this.loadError.set(err.status === 404 ? 'This service no longer exists.' : 'Could not load this service.');
      },
    });
  }

  hasUnsavedChanges(): boolean {
    if (this.loading() || this.loadError()) return false;
    return this.form.dirty
      || this.settingsGroup().dirty
      || this.fixesGroup().dirty
      || this.reasonsArray().dirty
      || this.linksArray().dirty;
  }

  isSaveInProgress(): boolean {
    return this.saving();
  }

  hasError(name: SetupControlName, error?: string): boolean {
    const control = this.form.controls[name];
    const visible = control.touched || this.submitted();
    return visible && (error ? control.hasError(error) : control.invalid);
  }

  onServiceIdInput(): void {
    this.serviceIdEdited = true;
  }

  normalizeServiceId(): void {
    const control = this.form.controls.serviceId;
    const normalized = control.value.trim().toLowerCase();
    if (normalized !== control.value) control.setValue(normalized);
  }

  save(): void {
    const code = this.isNew ? 'cdshooks.create' : 'cdshooks.edit';
    if (!this.actionGuard.ensure(code, `You do not have permission to ${this.isNew ? 'add' : 'edit'} CDS services.`)) return;

    this.submitted.set(true);
    this.errorMessage.set(null);
    this.normalizeServiceId();

    const settings = this.settingsGroup();
    const fixes = this.fixesGroup();
    const reasons = this.reasonsArray();
    const links = this.linksArray();
    const fixesInvalid = cdsFixesInvalid(this.fixTypes(), fixes);
    if (this.form.invalid || settings.invalid || fixesInvalid || reasons.invalid || links.invalid) {
      this.form.markAllAsTouched();
      settings.markAllAsTouched();
      fixes.markAllAsTouched();
      reasons.markAllAsTouched();
      links.markAllAsTouched();
      this.errorMessage.set('Some fields need attention. Check the highlighted fields.');
      return;
    }

    const value = this.form.getRawValue();
    const request: SaveCdsServiceInstanceRequest = {
      serviceId: value.serviceId,
      title: value.title.trim(),
      handlerKey: value.handlerKey,
      hook: value.hook,
      description: value.description.trim() || null,
      isEnabled: value.isEnabled,
      settings: readCdsSettings(this.handler()?.settings ?? [], settings),
      cardSourceLabel: value.cardSourceLabel.trim() || null,
      cardSourceUrl: value.cardSourceUrl.trim() || null,
      allowedIssuerIds: value.allowedIssuerIds,
      fetchMissingData: value.fetchMissingData,
      fixes: readCdsFixes(this.fixTypes(), fixes),
      overrideReasons: readCdsReasons(reasons),
      links: readCdsLinks(links),
    };

    // Asks first when this save is what turns fixes on: from then on, clinicians' clicks change EHR drafts.
    this.fixesConfirm
      .confirmSave(this.instance()?.fixesEnabled === true, request.fixes?.enabled === true)
      .subscribe(confirmed => {
        if (confirmed) this.send(request);
      });
  }

  private send(request: SaveCdsServiceInstanceRequest): void {
    const settings = this.settingsGroup();
    this.saving.set(true);
    const request$ = this.instanceId
      ? this.api.updateService(this.instanceId, request)
      : this.api.createService(request);

    request$.subscribe({
      next: saved => {
        this.saving.set(false);
        this.submitted.set(false);
        if (this.isNew) {
          this.form.markAsPristine();
          settings.markAsPristine();
          this.fixesGroup().markAsPristine();
          this.reasonsArray().markAsPristine();
          this.linksArray().markAsPristine();
          this.toast.success(`"${saved.title}" added.`);
          this.router.navigate(['/clinical-decision-support/services', saved.id], { replaceUrl: true });
        } else {
          const lostSignOff = this.instance()?.isClinicallySignedOff === true && !saved.isClinicallySignedOff;
          this.applyInstance(saved);
          this.toast.success('Changes saved.');
          if (lostSignOff) {
            this.toast.warning('The clinical sign-off was removed because what clinicians see changed. Have it reviewed again.');
          }
        }
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.errorMessage.set(err.message || 'Could not save the service.');
      },
    });
  }

  /** A sign-off was recorded or removed: only the sign-off changed, so the form (and any edits in it) stays as is. */
  onSignOffChanged(updated: CdsServiceInstance): void {
    this.instance.set(updated);
  }

  cancel(): void {
    this.router.navigate(['/clinical-decision-support/services']);
  }

  private applyInstance(instance: CdsServiceInstance): void {
    this.instance.set(instance);
    this.serviceIdEdited = true;
    this.form.setValue({
      handlerKey: instance.handlerKey,
      hook: instance.hook,
      title: instance.title,
      serviceId: instance.serviceId,
      description: instance.description,
      isEnabled: instance.isEnabled,
      cardSourceLabel: instance.cardSourceLabel,
      cardSourceUrl: instance.cardSourceUrl ?? '',
      allowedIssuerIds: [...instance.allowedIssuerIds],
      fetchMissingData: instance.fetchMissingData ?? true,
    });
    // The check type is fixed once a service exists: its settings belong to that type.
    this.form.controls.handlerKey.disable({ emitEvent: false });
    this.rebuildSettings(instance.settings);
    this.rebuildFixes(instance.fixes);
    this.reasonsArray.set(this.disabledIfReadOnly(buildCdsReasonsArray(instance)));
    this.linksArray.set(this.disabledIfReadOnly(buildCdsLinksArray(instance)));
    this.form.markAsPristine();
  }

  private applyNew(requestedHandler: string | null): void {
    const handler = this.handlers().find(h => h.key === requestedHandler) ?? this.handlers()[0];
    if (!handler) return;
    this.form.setValue({
      handlerKey: handler.key,
      hook: handler.supportedHooks[0] ?? '',
      title: handler.displayName,
      serviceId: toCdsServiceId(handler.displayName),
      description: '',
      isEnabled: true,
      cardSourceLabel: '',
      cardSourceUrl: '',
      allowedIssuerIds: [],
      fetchMissingData: true,
    });
    this.rebuildSettings(null);
    this.rebuildFixes(null);
    this.form.markAsPristine();
  }

  private rebuildSettings(values: CdsServiceInstance['settings'] | null): void {
    this.settingsGroup.set(buildCdsSettingsGroup(this.handler()?.settings ?? [], values));
  }

  /** Fixes belong to the check type, so they are rebuilt (off) whenever a new service switches check. */
  private rebuildFixes(fixes: CdsFixSettings | null): void {
    this.fixesGroup.set(this.disabledIfReadOnly(buildCdsFixesGroup(this.fixTypes(), fixes)));
  }

  /** A rebuilt form after a save keeps the page read-only for users who can't save. */
  private disabledIfReadOnly<T extends AbstractControl>(control: T): T {
    if (!this.canSave()) control.disable({ emitEvent: false });
    return control;
  }

  /** New services only: switching the check resets its settings to that check's defaults and picks a hook it supports. */
  private watchHandlerChanges(): void {
    this.form.controls.handlerKey.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        const handler = this.handler();
        if (!handler) return;
        if (!handler.supportedHooks.includes(this.form.controls.hook.value)) {
          this.form.controls.hook.setValue(handler.supportedHooks[0] ?? '');
        }
        this.rebuildSettings(null);
        this.rebuildFixes(null);
      });
  }

  /** New services only: the id follows the name until the user edits the id themselves. */
  private watchTitleChanges(): void {
    this.form.controls.title.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(title => {
        if (this.serviceIdEdited) return;
        this.form.controls.serviceId.setValue(toCdsServiceId(title));
      });
  }
}
