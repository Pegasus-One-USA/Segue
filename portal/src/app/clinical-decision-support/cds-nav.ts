export interface CdsTab {
  label: string;
  /** Absolute route, so the strip works the same from any child page. */
  route: string;
  icon: string;
  permissions: string[];
  /** When true, every permission is needed (matches the route's requireAll). */
  requireAll?: boolean;
}

/** The Clinical Decision Support tab strip. Each entry's permissions match its route's data in cds.routes.ts. */
export const CDS_TABS: CdsTab[] = [
  { label: 'Services', route: '/clinical-decision-support/services', icon: 'medical_services', permissions: ['cdshooks.view'] },
  { label: 'Connect your EHR', route: '/clinical-decision-support/connect', icon: 'cable', permissions: ['cdshooks.view'] },
  // Test lists the services to pick from, so it needs view as well as test.
  { label: 'Test', route: '/clinical-decision-support/test', icon: 'science', permissions: ['cdshooks.view', 'cdshooks.test'], requireAll: true },
  { label: 'Coverage (CRD)', route: '/clinical-decision-support/coverage', icon: 'health_and_safety', permissions: ['cdshooks.view'] },
  { label: 'Activity', route: '/clinical-decision-support/activity', icon: 'history', permissions: ['cdshooks.view'] },
];
