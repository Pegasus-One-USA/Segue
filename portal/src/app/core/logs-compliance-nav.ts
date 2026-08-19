export interface LogsComplianceTab {
  label: string;
  /** Absolute route (leading slash) so the same tab strip works identically whichever shell
   *  (governance-shell.component.ts or operations-shell.component.ts) currently renders it. */
  route: string;
  icon: string;
  permissions?: string[];
}

/**
 * The single "Logs & Compliance" tab strip, shared verbatim by governance-shell.component.ts and
 * operations-shell.component.ts so the header menu stays identical no matter which of the two
 * (route-wise still separate) shells is currently mounted — clicking "Errors" from Governance lands on
 * operations-shell, and clicking any of the other three from there lands back on governance-shell, but
 * the visible tab bar never changes. Every log this list doesn't surface directly is fully searchable
 * by Correlation ID; Compliance Reports is the one exception kept for its own sake (a PDF export across
 * a date range, not a per-row log, so Correlation Search has no equivalent for it).
 */
export const LOGS_COMPLIANCE_TABS: LogsComplianceTab[] = [
  { label: 'Audit Logs', route: '/governance/audit-logs', icon: 'history_edu', permissions: ['governance.read'] },
  // Re-surfaced per user direction — SSO troubleshooting needs to browse recent attempts
  // chronologically, which Correlation Search doesn't support without already knowing a correlation ID.
  { label: 'Authentication Logs', route: '/governance/authentication-logs', icon: 'verified_user', permissions: ['governance.read'] },
  { label: 'Correlation Search', route: '/governance/correlation-search', icon: 'search', permissions: ['governance.read'] },
  { label: 'Errors', route: '/operations/errors', icon: 'error_outline', permissions: ['governance.read'] },
  { label: 'Compliance Reports', route: '/governance/compliance-reports', icon: 'description', permissions: ['governance.read'] },
];
