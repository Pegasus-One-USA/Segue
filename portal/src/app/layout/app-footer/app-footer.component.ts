import { Component, input, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { BrandingService } from '../../services/branding.service';
import { AuthStore } from '../../auth/store/auth.store';
import { VersionService } from '../../services/version.service';
import { environment } from '../../../environments/environment';

@Component({
  selector: 'app-footer',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './app-footer.component.html',
  styleUrl: './app-footer.component.scss',
})
export class AppFooterComponent {
  /** 'full' — global shell footer (company · powered-by · version · support · website).
   *  'compact' — auth pages: just "Powered by Segue". */
  readonly variant = input<'full' | 'compact'>('full');

  protected readonly branding      = inject(BrandingService);
  protected readonly authStore     = inject(AuthStore);
  // Supersedes the earlier `appVersion = APP_VERSION` field: VersionService still exposes that same
  // build-time constant as `portalVersion` (what the footer strip shows), and adds the API's own
  // version for the build-details panel. The two can legitimately differ — a partial upgrade, or a
  // cached bundle against an upgraded API — so both are kept rather than collapsed into one number.
  protected readonly version       = inject(VersionService);
  protected readonly copyrightYear = new Date().getFullYear();

  /** Build details panel, opened by clicking the version. Closed by default — the footer stays a
   *  one-line strip, and the detail an operator needs for a support ticket is one click away. */
  protected readonly showDetails = signal(false);

  protected toggleDetails(): void {
    this.showDetails.update(open => !open);
  }

  // Non-production gets a visible badge — a safety signal for admins — while
  // production stays clean. Optional by design: null renders nothing.
  protected readonly envLabel: string | null = environment.production ? null : 'Development';
}
