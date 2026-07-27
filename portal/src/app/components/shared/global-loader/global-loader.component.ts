import { Component, inject } from '@angular/core';
import { LoadingService } from '../../../services/loading.service';
import { BrandingService } from '../../../services/branding.service';

/**
 * App-wide "an API call is in flight" indicator — mounted once at the root (see
 * AppComponent) so it covers every route without any page/component needing to
 * wire up its own loading state. Driven entirely by LoadingService's in-flight
 * request count (populated by loadingInterceptor) and the tenant's own
 * BrandConfiguration.loaderStyle, so it's both automatic and on-brand.
 */
@Component({
  selector: 'app-global-loader',
  standalone: true,
  imports: [],
  templateUrl: './global-loader.component.html',
  styleUrl: './global-loader.component.scss',
})
export class GlobalLoaderComponent {
  protected readonly loading = inject(LoadingService);
  protected readonly branding = inject(BrandingService);
}
