import { Component, input, inject } from '@angular/core';
import { BrandingService } from '../../services/branding.service';
import { AuthStore } from '../../auth/store/auth.store';
import { environment } from '../../../environments/environment';
import { APP_VERSION } from '../../version';

@Component({
  selector: 'app-footer',
  standalone: true,
  templateUrl: './app-footer.component.html',
  styleUrl: './app-footer.component.scss',
})
export class AppFooterComponent {
  /** 'full' — global shell footer (company · powered-by · version · support · website).
   *  'compact' — auth pages: just "Powered by Segue". */
  readonly variant = input<'full' | 'compact'>('full');

  protected readonly branding      = inject(BrandingService);
  protected readonly authStore     = inject(AuthStore);
  protected readonly appVersion    = APP_VERSION;
  protected readonly copyrightYear = new Date().getFullYear();

  // Non-production gets a visible badge — a safety signal for admins — while
  // production stays clean. Optional by design: null renders nothing.
  protected readonly envLabel: string | null = environment.production ? null : 'Development';
}
