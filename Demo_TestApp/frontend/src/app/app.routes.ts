import { Routes } from '@angular/router';

// No page-based navigation in this app — screens are swapped via signals in App (see app.ts/app.html) based on
// the selected Login Type. Router is only bootstrapped so ActivatedRoute resolves real query params (iss, launch,
// workflowRunId) for components that need them, e.g. LaunchProviderInAppComponent.
export const routes: Routes = [];
