import { Component, inject, OnInit, OnDestroy, DestroyRef, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { debounceTime } from 'rxjs';
import { BrandAssetFieldComponent } from '../../components/brand-asset-field/brand-asset-field.component';
import { BrandingService } from '../../../services/branding.service';
import { ThemeService } from '../../../services/theme.service';
import { BrandConfiguration, BrandThemeMode } from '../../../models/brand-configuration.model';

const HEX_COLOR_PATTERN = /^#[0-9A-Fa-f]{6}$/;

@Component({
  selector:    'app-branding-settings',
  standalone:  true,
  imports:     [ReactiveFormsModule, BrandAssetFieldComponent],
  templateUrl: './branding-settings.component.html',
  styleUrl:    './branding-settings.component.scss',
})
export class BrandingSettingsComponent implements OnInit, OnDestroy {
  private readonly fb          = inject(FormBuilder);
  private readonly destroyRef  = inject(DestroyRef);
  protected readonly branding  = inject(BrandingService);
  private readonly themeService = inject(ThemeService);

  protected readonly saved  = signal(false);
  protected readonly saving = signal(false);

  protected readonly themeModeOptions: { id: BrandThemeMode; label: string }[] = [
    { id: 'light',  label: 'Light' },
    { id: 'dark',   label: 'Dark' },
    { id: 'system', label: 'System' },
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
    this.originalConfig    = this.branding.current();
    this.originalThemeMode = this.themeService.mode();
    this.populateForm(this.originalConfig);
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
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.saving.set(true);
    this.branding.save(this.buildConfig()).subscribe(saved => {
      this.originalConfig    = saved;
      this.originalThemeMode = saved.defaultThemeMode;
      this.savedThisSession  = true;
      this.saving.set(false);
      this.saved.set(true);
      setTimeout(() => this.saved.set(false), 3000);
    });
  }

  protected resetToDefault(): void {
    this.branding.resetToDefault();
    this.originalConfig    = this.branding.current();
    this.originalThemeMode = this.originalConfig.defaultThemeMode;
    this.savedThisSession  = true; // the reset itself is the intended persisted state
    this.themeService.set(this.originalThemeMode);
    this.populateForm(this.originalConfig);
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
