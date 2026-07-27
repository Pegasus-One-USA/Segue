import { Injectable, inject, signal, computed, effect } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of, delay, tap, catchError } from 'rxjs';
import { AuthStore } from '../auth/store/auth.store';
import { ThemeService } from './theme.service';
import { LoadingService } from './loading.service';
import { BrandConfiguration, DEFAULT_BRANDING } from '../models/brand-configuration.model';

const STORAGE_PREFIX = 'fhirbridge.branding.';

/**
 * White-label branding. Resolves a tenant's BrandConfiguration and applies it by
 * writing the --brand-* CSS custom properties (see styles.scss TIER 1.5) onto
 * <html> — every existing component already themes off the --color-* tokens
 * that derive from those, so nothing else needs to change per tenant.
 *
 * Backend is not ready: `resolve()` reads mock JSON fixtures under
 * public/mock/branding/{tenantId}.json. This is the ONLY method whose internals
 * change once a real branding API exists — its signature and every caller stay
 * identical (mirrors how `TenantRoleService` already mocks tenants locally).
 */
@Injectable({ providedIn: 'root' })
export class BrandingService {
  private readonly http    = inject(HttpClient);
  private readonly auth    = inject(AuthStore);
  private readonly theme   = inject(ThemeService);
  private readonly loading = inject(LoadingService);

  readonly current   = signal<BrandConfiguration>(DEFAULT_BRANDING);
  readonly isDefault = computed(() => this.current().tenantId === 'default');

  /** Sidebar/auth-header logo: swaps to the dark logo once dark theme is actually
   *  showing (resolves 'system' correctly), falling back to the light logo if no
   *  dark variant was uploaded. This is the one thing every logo consumer should
   *  read instead of `current().assets.logoUrl` directly. */
  readonly effectiveLogoUrl = computed(() => {
    const assets = this.current().assets;
    const isDark = this.theme.resolved() === 'dark';
    return (isDark && assets.darkLogoUrl) ? assets.darkLogoUrl : assets.logoUrl;
  });

  private lastAppliedTenant: string | null = null;

  constructor() {
    // Anonymous case (login / forgot-password / set-password): resolve from
    // ?tenant=<slug> in the URL immediately, before the router renders anything —
    // instantiated eagerly in AppComponent, same pattern as ThemeService.
    this.resolveAndApply(this.readTenantFromUrl());

    // Authenticated case: re-resolve the moment the logged-in user's tenant is
    // known (covers both "restored an existing session" and "just logged in"),
    // and lets the real tenant override whatever a preview ?tenant= param showed.
    effect(() => {
      const orgId = this.auth.currentUser()?.orgId;
      if (orgId && orgId !== this.lastAppliedTenant) {
        this.resolveAndApply(orgId);
      }
    });
  }

  resolve(tenantId: string | null): Observable<BrandConfiguration> {
    const slug = tenantId?.trim() || 'default';
    return this.http.get<BrandConfiguration>(`mock/branding/${slug}.json`).pipe(
      catchError(() => this.http.get<BrandConfiguration>('mock/branding/default.json')),
      catchError(() => of(DEFAULT_BRANDING)),
    );
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

  /** Mock persistence: keeps the edit in localStorage per-tenant so it survives a reload. */
  save(config: BrandConfiguration): Observable<BrandConfiguration> {
    const saved: BrandConfiguration = { ...config, updatedAt: new Date().toISOString() };
    return this.loading.track(
      of(saved).pipe(
        delay(400),
        tap(v => {
          this.applyToDocument(v);
          this.persistLocal(v);
        }),
      ),
    );
  }

  resetToDefault(): void {
    const tenantId = this.current().tenantId;
    this.clearLocal(tenantId);
    this.applyToDocument({ ...DEFAULT_BRANDING, tenantId });
  }

  private resolveAndApply(tenantId: string | null): void {
    const slug = tenantId?.trim() || 'default';
    this.lastAppliedTenant = slug;

    const local = this.readLocal(slug);
    if (local) { this.applyToDocument(local); return; }

    this.resolve(slug).subscribe(config => this.applyToDocument(config));
  }

  private readTenantFromUrl(): string | null {
    if (typeof window === 'undefined') return null;
    return new URLSearchParams(window.location.search).get('tenant');
  }

  private persistLocal(config: BrandConfiguration): void {
    try { localStorage.setItem(STORAGE_PREFIX + config.tenantId, JSON.stringify(config)); }
    catch { /* storage unavailable (private mode) — non-fatal */ }
  }

  private readLocal(tenantId: string): BrandConfiguration | null {
    try {
      const raw = localStorage.getItem(STORAGE_PREFIX + tenantId);
      return raw ? JSON.parse(raw) as BrandConfiguration : null;
    } catch {
      return null;
    }
  }

  private clearLocal(tenantId: string): void {
    try { localStorage.removeItem(STORAGE_PREFIX + tenantId); }
    catch { /* ignore */ }
  }
}
