import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ThemeService } from './services/theme.service';
import { BrandingService } from './services/branding.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet],
  template: '<router-outlet />',
})
export class AppComponent {
  // Instantiate ThemeService at startup so the persisted theme is applied.
  private readonly theme = inject(ThemeService);
  // Instantiate BrandingService at startup so tenant branding is resolved and
  // applied before the router renders anything (including /auth/login).
  private readonly branding = inject(BrandingService);
}
