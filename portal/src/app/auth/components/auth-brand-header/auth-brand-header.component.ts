import { Component, inject } from '@angular/core';
import { BrandingService } from '../../../services/branding.service';

/**
 * Shared brand header for the auth flow (login / forgot-password / set-password) —
 * previously each page duplicated its own hardcoded "FHIRBridge" SVG + text block.
 * Reads BrandingService so a configured tenant's logo/name replace the FHIRBridge
 * default automatically, with no per-page changes.
 */
@Component({
  selector: 'app-auth-brand-header',
  standalone: true,
  templateUrl: './auth-brand-header.component.html',
  styleUrl: './auth-brand-header.component.scss',
})
export class AuthBrandHeaderComponent {
  protected readonly branding = inject(BrandingService);
}
