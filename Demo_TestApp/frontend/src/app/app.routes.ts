import { Routes } from '@angular/router';

// No page-based navigation in this app — screens are swapped via signals in App (see app.ts/app.html) based on
// the selected Login Type. Router is only bootstrapped so ActivatedRoute resolves real query params (iss, launch,
// workflowRunId) for components that need them, e.g. LaunchProviderInAppComponent.
//
// An empty route table only matches the root ('') path — hitting a real path like /launchproviderinapp (the
// EHR-launch redirect target) throws NG04002 ("Cannot match any routes"), which leaves ActivatedRoute's query
// param snapshot broken for every component that reads iss/launch/workflowRunId from it. A component-less
// wildcard route fixes this: it matches every path (there's no <router-outlet> anywhere, so it never renders
// anything) while still letting the Router resolve query params normally.
export const routes: Routes = [
  { path: '**', children: [] },
];
