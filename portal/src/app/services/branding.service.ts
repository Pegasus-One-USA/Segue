import { Injectable, inject, signal, computed } from '@angular/core';
import { HttpClient, HttpContext, HttpParams } from '@angular/common/http';
import { Observable, of, tap, map, catchError, timeout } from 'rxjs';
import { ThemeService } from './theme.service';
import { LoadingService } from './loading.service';
import { SKIP_LOADER } from '../core/loading.interceptor';
import { BRANDING_ENDPOINTS } from '../core/api-endpoints';
import { BrandConfiguration, BrandThemeMode, LoaderStyle, DEFAULT_BRANDING } from '../models/brand-configuration.model';

/** Wire shape of GET/PUT /api/v1/branding — mirrors BrandConfigurationDto (backend) field-for-field. */
interface BrandConfigurationDto {
  companyName: string;
  primaryColor: string;
  secondaryColor: string;
  accentColor: string;
  backgroundColor: string;
  fontFamily: string;
  footerText: string;
  supportEmail: string;
  supportPhone: string;
  website: string;
  emailFooterText: string;
  defaultThemeMode: string;
  loaderStyle: string;
  assets: {
    logoUrl: string;
    darkLogoUrl: string;
    faviconUrl: string;
    loginBackgroundUrl: string;
    loginIllustrationUrl: string;
    emailLogoUrl: string;
  };
  updatedOnUtc: string;
  /** False when nobody has ever saved a row — the backend's built-in-default fallback. */
  isConfigured: boolean;
}

/**
 * White-label branding. Resolves a Tenant's saved BrandConfiguration from the backend and applies it by
 * writing the --brand-* CSS custom properties (see styles.scss TIER 1.5) onto <html> — every existing
 * component already themes off the --color-* tokens that derive from those, so nothing else needs to change.
 *
 * The database (BrandConfigurations table, via GET/PUT /api/v1/branding) is the sole source of truth.
 * Tenant resolution happens entirely server-side (see BrandingController) — this service never resolves,
 * stores, or sends a tenant id of its own:
 *  - Authenticated: the backend derives the caller's own tenant from their session. No `?tenant=` is ever
 *    read once logged in — it must not override the real, authenticated tenant.
 *  - Anonymous (pre-login): forwards whatever `?tenant=<code>` is on the current URL, if any — that's how
 *    `/auth/login?tenant=abc` shows Tenant "abc"'s branding before authentication. No code — or a code that
 *    doesn't resolve — falls back to the built-in default, exactly like today.
 * `current().tenantId` is kept as a fixed 'default' purely so existing consumers of the BrandConfiguration
 * shape don't need touching; it carries no real meaning on this client.
 *
 * localStorage is NOT used for persistence — a previous version of this service kept edits in localStorage
 * only, which is exactly why a browser refresh used to revert to default branding: it was never actually
 * saved anywhere durable, and it raced against the anonymous/authenticated resolution paths that used to
 * exist here. This version fetches fresh from the backend on every app load; nothing local can go stale.
 */
@Injectable({ providedIn: 'root' })
export class BrandingService {
  private readonly http    = inject(HttpClient);
  private readonly theme   = inject(ThemeService);
  private readonly loading = inject(LoadingService);

  readonly current = signal<BrandConfiguration>(DEFAULT_BRANDING);
  /** True only when nobody has ever saved a branding row (the backend returned its built-in fallback) —
   *  not derived from `current()` itself, since a saved configuration could legitimately look identical to
   *  the built-in defaults. Driven by the GET/PUT response's own `isConfigured` flag. */
  private readonly configured = signal(false);
  readonly isDefault = computed(() => !this.configured());

  /** Sidebar/auth-header logo: swaps to the dark logo once dark theme is actually
   *  showing (resolves 'system' correctly), falling back to the light logo if no
   *  dark variant was uploaded. This is the one thing every logo consumer should
   *  read instead of `current().assets.logoUrl` directly. */
  readonly effectiveLogoUrl = computed(() => {
    const assets = this.current().assets;
    const isDark = this.theme.resolved() === 'dark';
    return (isDark && assets.darkLogoUrl) ? assets.darkLogoUrl : assets.logoUrl;
  });

  // Bootstrap resolution now happens in app.config.ts's APP_INITIALIZER (awaited there, before the
  // router's initialNavigation runs) rather than fire-and-forget here in the constructor — see that
  // file's initApp() for why. This service still has no opinion on WHEN it's first resolved; it just
  // exposes resolve()/current()/applyToDocument() as building blocks for whoever needs to trigger one
  // (bootstrap, and AuthService.resolveBrandingForNewSession() after a fresh login).

  /** Fetches the persisted branding from the backend. Falls back to the built-in defaults on any error
   *  (network failure, backend down, or a response that never arrives at all) rather than leaving the
   *  app unthemed. Forwards `?tenant=<code>` from the current URL if present — meaningful only pre-login
   *  (BrandingController ignores it once the caller is authenticated, resolving that user's real tenant
   *  instead).
   *
   *  SKIP_LOADER: branding is non-critical UI configuration (company name/logo/colors) — a slow or
   *  hanging request here must never hold the app-wide loader open and block every other page from
   *  ever looking "done". See loading.interceptor.ts's SKIP_LOADER doc comment.
   *
   *  timeout(): this is now awaited as part of app.config.ts's APP_INITIALIZER, blocking the router's
   *  initialNavigation until it settles — a bounded timeout here is what keeps a genuinely hung request
   *  (observed at least once during development) from blocking app boot forever; falling through to the
   *  same catchError as any other failure. */
  resolve(): Observable<BrandConfiguration> {
    const tenantCode = this.readTenantCodeFromUrl();
    const params = tenantCode ? new HttpParams().set('tenant', tenantCode) : undefined;
    const context = new HttpContext().set(SKIP_LOADER, true);

    return this.http.get<BrandConfigurationDto>(BRANDING_ENDPOINTS.get, { params, context }).pipe(
      timeout(5000),
      tap(dto => this.configured.set(dto.isConfigured)),
      map(dto => this.fromDto(dto)),
      catchError(() => { this.configured.set(false); return of(DEFAULT_BRANDING); }),
    );
  }

  private readTenantCodeFromUrl(): string | null {
    if (typeof window === 'undefined') return null;
    return new URLSearchParams(window.location.search).get('tenant');
  }

  /** Writes the brand tier onto <html>, swaps the document title/favicon, and updates `current`. */
  applyToDocument(config: BrandConfiguration): void {
    this.current.set(config);
    if (typeof document === 'undefined') return;

    const root = document.documentElement.style;
    root.setProperty('--brand-primary',    config.primaryColor);
    root.setProperty('--brand-secondary',  config.secondaryColor);
    root.setProperty('--brand-accent',     config.accentColor);
    root.setProperty('--brand-background', config.backgroundColor);
    if (config.fontFamily?.trim()) {
      root.setProperty('--brand-font', config.fontFamily);
    } else {
      root.removeProperty('--brand-font');
    }

    document.title = config.companyName;
    const favicon = document.querySelector<HTMLLinkElement>("link[rel='icon']");
    if (favicon && config.assets.faviconUrl) favicon.href = config.assets.faviconUrl;
  }

  /** Persists to the database via PUT /api/v1/branding, then applies the backend's own response (not the
   *  request the caller sent) — the acceptance criterion this exists for is SAVE -> DATABASE -> REFRESH ->
   *  GET FROM BACKEND -> SAME BRANDING, so the UI must reflect what the server actually stored, not merely
   *  what the client optimistically assumed would be stored. */
  save(config: BrandConfiguration): Observable<BrandConfiguration> {
    return this.loading.track(this.persist(config));
  }

  /** Reverts to the built-in defaults immediately (live preview), then persists that reset to the
   *  database in the background — persisting matters here too: without it, a refresh would reload
   *  whatever was previously saved and silently undo the reset. */
  resetToDefault(): void {
    this.applyToDocument({ ...DEFAULT_BRANDING });
    this.persist(DEFAULT_BRANDING).subscribe({
      error: () => { /* best-effort; the live preview already reverted, next explicit Save will retry */ },
    });
  }

  private persist(config: BrandConfiguration): Observable<BrandConfiguration> {
    return this.http.put<BrandConfigurationDto>(BRANDING_ENDPOINTS.update, this.toRequestBody(config)).pipe(
      tap(dto => this.configured.set(dto.isConfigured)),
      map(dto => this.fromDto(dto)),
      tap(saved => this.applyToDocument(saved)),
    );
  }

  private fromDto(dto: BrandConfigurationDto): BrandConfiguration {
    return {
      tenantId:         'default',
      companyName:      dto.companyName,
      primaryColor:     dto.primaryColor,
      secondaryColor:   dto.secondaryColor,
      accentColor:      dto.accentColor,
      backgroundColor:  dto.backgroundColor,
      fontFamily:       dto.fontFamily,
      footerText:       dto.footerText,
      supportEmail:     dto.supportEmail,
      supportPhone:     dto.supportPhone,
      website:          dto.website,
      emailFooterText:  dto.emailFooterText,
      defaultThemeMode: dto.defaultThemeMode as BrandThemeMode,
      loaderStyle:      dto.loaderStyle as LoaderStyle,
      assets: { ...dto.assets },
      updatedAt: dto.updatedOnUtc,
    };
  }

  private toRequestBody(config: BrandConfiguration) {
    return {
      companyName:      config.companyName,
      primaryColor:     config.primaryColor,
      secondaryColor:   config.secondaryColor,
      accentColor:      config.accentColor,
      backgroundColor:  config.backgroundColor,
      fontFamily:       config.fontFamily,
      footerText:       config.footerText,
      supportEmail:     config.supportEmail,
      supportPhone:     config.supportPhone,
      website:          config.website,
      emailFooterText:  config.emailFooterText,
      defaultThemeMode: config.defaultThemeMode,
      loaderStyle:      config.loaderStyle,
      assets:           { ...config.assets },
    };
  }
}
