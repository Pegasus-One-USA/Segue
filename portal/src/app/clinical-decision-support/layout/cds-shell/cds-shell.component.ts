import { Component, computed, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { AuthStore } from '../../../auth/store/auth.store';
import { CDS_TABS } from '../../cds-nav';

/** Tab strip for Clinical Decision Support. Same visibility rule as permissionGuard, so a tab never leads to /unauthorized. */
@Component({
  selector: 'app-cds-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet, MatIconModule],
  templateUrl: './cds-shell.component.html',
  styleUrl: './cds-shell.component.scss',
})
export class CdsShellComponent {
  private readonly store = inject(AuthStore);

  readonly tabs = computed(() =>
    CDS_TABS.filter(tab => {
      if (this.store.isAdmin()) return true;
      return tab.requireAll
        ? tab.permissions.every(p => this.store.hasPermission(p))
        : tab.permissions.some(p => this.store.hasPermission(p));
    })
  );
}
