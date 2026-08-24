import { Component, effect, inject, OnInit, OnDestroy, DestroyRef, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { debounceTime } from 'rxjs';
import { HasUnsavedChanges } from '../../../core/guards/has-unsaved-changes';
import { UnsavedChangesRegistryService } from '../../../core/services/unsaved-changes-registry.service';
import { BrandAssetFieldComponent } from '../../components/brand-asset-field/brand-asset-field.component';
import { BrandingService } from '../../../services/branding.service';
import { ThemeService } from '../../../services/theme.service';
import { ToastService } from '../../../services/toast.service';
import { BrandConfiguration, BrandThemeMode, LoaderStyle } from '../../../models/brand-configuration.model';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';

const HEX_COLOR_PATTERN = /^#[0-9A-Fa-f]{6}$/;

@Component({
  selector:    'app-branding-settings',
  standalone:  true,
  imports:     [ReactiveFormsModule, BrandAssetFieldComponent],
  templateUrl: './branding-settings.component.html',
  styleUrl:    './branding-settings.component.scss',
})
export class BrandingSettingsComponent implements OnInit, OnDestroy, HasUnsavedChanges {
  private readonly fb          = inject(FormBuilder);
  private readonly destroyRef  = inject(DestroyRef);
  protected readonly branding  = inject(BrandingService);
  private readonly themeService = inject(ThemeService);
  private readonly toast = inject(ToastService);
  private readonly permissions = inject(PermissionService);
  private readonly actionGuard = inject(PermissionActionGuard);

  /** The route already requires configuration.write to enter this screen at all, so this is
   *  defense-in-depth (a permission revoked in another tab while this one stays open) rather than
   *  the only line of defense — same reasoning as every other "route already gates this" case
   *  elsewhere in the app. Disabling the whole form (rather than gating each of its ~17 controls
   *  individually) also disables every embedded app-brand-asset-field's Upload/Clear via the
   *  standard ControlValueAccessor.setDisabledState() protocol — no per-field wiring needed. */
  protected readonly canWrite = this.permissions.hasPermission('configuration.write');

  protected readonly saving = signal(false);

  private readonly unsavedChangesRegistry = inject(UnsavedChangesRegistryService);

  constructor() {
    this.unsavedChangesRegistry.register(
      () => this.hasUnsavedChanges() || this.isSaveInProgress(),
      this.destroyRef,
    );

    // BrandingService.current() only reflects the real, persisted branding once its own bootstrap-time
    // resolve() GET (fired from AppComponent) actually returns — on a hard refresh landing directly on
    // this page, that GET can still be in flight when this component constructs. A one-time ngOnInit()
    // snapshot could therefore capture DEFAULT_BRANDING instead of the saved values, and nothing would
    // ever re-populate the form once the real response arrived a moment later — exactly the "branding
    // reverts after refresh" bug. Reacting to every change instead fixes both cases (already resolved,
    // or resolving later) — guarded by form.dirty so a later update never clobbers an edit in progress.
    effect(() => {
      const cfg = this.branding.current();
      if (this.form.dirty) return;
      this.originalConfig = cfg;
      this.populateForm(cfg);
    });
  }

  protected readonly themeModeOptions: { id: BrandThemeMode; label: string }[] = [
    { id: 'light',  label: 'Light' },
    { id: 'dark',   label: 'Dark' },
    { id: 'system', label: 'System' },
  ];

  protected readonly loaderStyleOptions: { id: LoaderStyle; label: string }[] = [
    { id: 'bar',     label: 'Top bar' },
    { id: 'spinner', label: 'Spinner' },
    { id: 'list',    label: 'List scan' },
    { id: 'none',    label: 'None' },
  ];

  protected readonly fontOptions = [
    { value: '',                       label: 'Platform default (Inter)' },
    { value: "'Roboto', sans-serif",   label: 'Roboto' },
    { value: "'Georgia', serif",       label: 'Georgia' },
    { value: "'Poppins', sans-serif",  label: 'Poppins' },
  ];

  protected readonly form = this.fb.nonNullable.group({
    companyName:      ['', Validators.required],
    primaryColor:     ['#00A89D', [Validators.required, Validators.pattern(HEX_COLOR_PATTERN)]],
    secondaryColor:   ['#0076A8', [Validators.required, Validators.pattern(HEX_COLOR_PATTERN)]],
    accentColor:      ['#007A72', [Validators.required, Validators.pattern(HEX_COLOR_PATTERN)]],
    backgroundColor:  ['#F5F7FA', [Validators.required, Validators.pattern(HEX_COLOR_PATTERN)]],
    fontFamily:       [''],
    footerText:       [''],
    supportEmail:     ['', Validators.email],
    supportPhone:     [''],
    website:          [''],
    emailFooterText:  [''],
    defaultThemeMode: ['light' as BrandThemeMode],
    loaderStyle:      ['bar' as LoaderStyle],
    logoUrl:              [''],
    darkLogoUrl:          [''],
    faviconUrl:           [''],
    loginBackgroundUrl:   [''],
    loginIllustrationUrl: [''],
    emailLogoUrl:         [''],
  });

  private originalConfig: BrandConfiguration = this.branding.current();
  private originalThemeMode: BrandThemeMode  = 'light';
  private savedThisSession = false;

  ngOnInit(): void {
    this.originalThemeMode = this.themeService.mode();
    if (!this.canWrite) { this.form.disable({ emitEvent: false }); }
    // The pills reflect what's actually on screen right now, not just whatever
    // was last saved into the branding record — avoids showing "Light" active
    // while the page is actually rendering in dark mode.
    this.form.controls.defaultThemeMode.setValue(this.originalThemeMode, { emitEvent: false });

    // Live preview: every edit re-applies the brand tier immediately, so the real
    // sidebar/header/footer around this very page (plus the widget below) update
    // before anything is saved.
    this.form.valueChanges
      .pipe(debounceTime(120), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.branding.applyToDocument(this.buildConfig()));
  }

  ngOnDestroy(): void {
    // Leaving without saving reverts both the branding preview and the previewed
    // theme mode back to what was actually active before this page was opened.
    if (!this.savedThisSession) {
      this.branding.applyToDocument(this.originalConfig);
      this.themeService.set(this.originalThemeMode);
    }
  }

  /** Applies the theme mode immediately (same live-preview spirit as the color pickers). */
  protected setThemeMode(mode: BrandThemeMode): void {
    this.form.controls.defaultThemeMode.setValue(mode);
    this.themeService.set(mode);
  }

  protected save(): void {
    if (!this.actionGuard.ensure('configuration.write', 'You do not have permission to modify branding.')) return;
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.saving.set(true);
    this.branding.save(this.buildConfig()).subscribe({
      next: (saved) => {
        this.originalConfig    = saved;
        this.originalThemeMode = saved.defaultThemeMode;
        this.savedThisSession  = true;
        this.saving.set(false);
        this.form.markAsPristine();
        this.toast.success('Branding saved');
      },
      // Now that save() actually round-trips to the backend, it can genuinely fail (validation, network,
      // permission) — previously the mock implementation never errored, so there was no error path here.
      error: (e) => {
        this.saving.set(false);
        this.toast.error('Save failed', e?.error?.message ?? 'Could not save branding. Please try again.');
      },
    });
  }

  protected resetToDefault(): void {
    if (!this.actionGuard.ensure('configuration.write', 'You do not have permission to modify branding.')) return;
    this.branding.resetToDefault();
    this.originalConfig    = this.branding.current();
    this.originalThemeMode = this.originalConfig.defaultThemeMode;
    this.savedThisSession  = true; // the reset itself is the intended persisted state
    this.themeService.set(this.originalThemeMode);
    this.populateForm(this.originalConfig);
    this.form.markAsPristine();
    this.toast.success('Branding reset to default');
  }

  // ── HasUnsavedChanges (unsaved-changes.guard.ts) ────────────────────────────
  hasUnsavedChanges(): boolean {
    return this.form.dirty;
  }

  isSaveInProgress(): boolean {
    return this.saving();
  }

  /** Mirrors BrandingService.effectiveLogoUrl, but off the draft form value so the
   *  preview reflects what you're currently editing, not just what's already saved. */
  protected previewLogoUrl(): string {
    const v = this.form.value;
    const isDark = this.themeService.resolved() === 'dark';
    return (isDark && v.darkLogoUrl) ? v.darkLogoUrl : (v.logoUrl ?? '');
  }

  private populateForm(cfg: BrandConfiguration): void {
    this.form.patchValue({
      companyName:      cfg.companyName,
      primaryColor:     cfg.primaryColor,
      secondaryColor:   cfg.secondaryColor,
      accentColor:      cfg.accentColor,
      backgroundColor:  cfg.backgroundColor,
      fontFamily:       cfg.fontFamily,
      footerText:       cfg.footerText,
      supportEmail:     cfg.supportEmail,
      supportPhone:     cfg.supportPhone,
      website:          cfg.website,
      emailFooterText:  cfg.emailFooterText,
      defaultThemeMode: cfg.defaultThemeMode,
      loaderStyle:      cfg.loaderStyle,
      logoUrl:              cfg.assets.logoUrl,
      darkLogoUrl:          cfg.assets.darkLogoUrl,
      faviconUrl:           cfg.assets.faviconUrl,
      loginBackgroundUrl:   cfg.assets.loginBackgroundUrl,
      loginIllustrationUrl: cfg.assets.loginIllustrationUrl,
      emailLogoUrl:         cfg.assets.emailLogoUrl,
    }, { emitEvent: false });
  }

  private buildConfig(): BrandConfiguration {
    const v = this.form.getRawValue();
    return {
      tenantId:         this.originalConfig.tenantId,
      companyName:      v.companyName,
      primaryColor:     v.primaryColor,
      secondaryColor:   v.secondaryColor,
      accentColor:      v.accentColor,
      backgroundColor:  v.backgroundColor,
      fontFamily:       v.fontFamily,
      footerText:       v.footerText,
      supportEmail:     v.supportEmail,
      supportPhone:     v.supportPhone,
      website:          v.website,
      emailFooterText:  v.emailFooterText,
      defaultThemeMode: v.defaultThemeMode,
      loaderStyle:      v.loaderStyle,
      assets: {
        logoUrl:              v.logoUrl,
        darkLogoUrl:          v.darkLogoUrl,
        faviconUrl:           v.faviconUrl,
        loginBackgroundUrl:   v.loginBackgroundUrl,
        loginIllustrationUrl: v.loginIllustrationUrl,
        emailLogoUrl:         v.emailLogoUrl,
      },
      updatedAt: this.originalConfig.updatedAt,
    };
  }
}
