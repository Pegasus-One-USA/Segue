import { Component, inject, computed } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { AuthStore } from '../../auth/store/auth.store';
import { LOGS_COMPLIANCE_TABS } from '../../core/logs-compliance-nav';

// This shell also hosts routes beyond the ones surfaced in the shared "Logs & Compliance" tab strip
// (LOGS_COMPLIANCE_TABS, imported below) — audit-logs and correlation-search etc. resolve as this
// shell's own children per app.routes.ts, while authentication-logs, smart-launch-logs, oauth-logs,
// authorization-logs, data-access-logs, security-events, retention-policies, archive, log-settings,
// alerts, and alert-rules stay reachable by URL with no menu entry. OAuth is a pure filtered subset of
// Authentication Logs (same table, AuthenticationType starting with "OAuth"), carrying no data the
// Authentication Logs page doesn't already have. Archive is hidden per user direction — its restore
// action is a 501 stub, so surfacing it invites a "why doesn't restore work" support ticket before
// that's implemented. Retention Policies and Log Settings are hidden per user direction too, as are
// Alerts, Alert Rules, and Data Access Logs. Authentication Logs, SMART Launch Logs, Authorization
// Logs, and Security Events are hidden per user direction as well, since every one of them is fully
// searchable by Correlation ID via the Correlation Search tab.

@Component({
  selector: 'app-governance-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet, MatIconModule],
  templateUrl: './governance-shell.component.html',
  styleUrl: './governance-shell.component.scss',
})
export class GovernanceShellComponent {
  private readonly store = inject(AuthStore);

  // The shared "Logs & Compliance" tab strip (see logs-compliance-nav.ts) — identical whether this
  // shell or operations-shell.component.ts is currently mounted, so the header menu never appears to
  // shrink just because a tab (e.g. Errors) happens to route into the other shell.
  readonly tabs = computed(() =>
    LOGS_COMPLIANCE_TABS.filter(tab => {
      if (!tab.permissions?.length) return true;
      if (this.store.isAdmin()) return true;
      return tab.permissions.some(p => this.store.hasPermission(p));
    })
  );
}
