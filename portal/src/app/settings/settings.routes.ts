import { Routes } from '@angular/router';
import { permissionGuard } from '../auth/guards/permission.guard';
import { superAdminGuard } from '../auth/guards/super-admin.guard';
import { unsavedChangesGuard } from '../core/guards/unsaved-changes.guard';
import { settingsLandingGuard } from './guards/settings-landing.guard';

// Terminology Codes: each of the four import systems has its own independent View/Write pair
// (loinc.*/snomedct.*/rxnorm.*/icd10.*, split off from a shared "TerminologyCodes" group, itself
// originally split off from Email's configuration.view/write) — kept as one list here since every
// gate that needs "can this role reach ANY terminology system" (the shell route, its own landing
// redirect) uses the exact same OR across all eight codes.
const TERMINOLOGY_PERMISSIONS = [
  'loinc.view', 'loinc.write',
  'snomedct.view', 'snomedct.write',
  'rxnorm.view', 'rxnorm.write',
  'icd10.view', 'icd10.write',
];

// Email + Terminology Codes together — every permission that can unlock some part of the
// System Settings shell without the SuperAdmin role (General/Security stay role-only; see below).
const SYSTEM_SETTINGS_PERMISSIONS = ['configuration.view', 'configuration.write', ...TERMINOLOGY_PERMISSIONS];

// Every child below keeps the exact guard/permission it had as a standalone top-level route
// before consolidation under this shell — see docs/backend/12-provider-standalone-ehr-launch-fixes.md.
export const SETTINGS_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./layout/settings-shell.component').then(m => m.SettingsShellComponent),
    children: [
      {
        path: 'branding',
        canActivate: [permissionGuard],
        canDeactivate: [unsavedChangesGuard],
        data: { permissions: ['configuration.write'] },
        loadComponent: () =>
          import('./pages/branding/branding-settings.component').then(m => m.BrandingSettingsComponent),
      },
      {
        path: 'ehr-endpoints',
        canActivate: [permissionGuard],
        data: { permissions: ['ehrendpoints.view'] },
        loadComponent: () =>
          import('../ehr-endpoints/pages/ehr-endpoint-list/ehr-endpoint-list.component').then(
            m => m.EhrEndpointListComponent
          ),
      },
      {
        // Merges the formerly-standalone Source Connections, Destination Connections, and Mapping
        // Profiles tabs into one screen with a section per former tab — grouped because all three
        // configure the data a workflow moves through (source -> mapping -> destination), not because
        // they share a permission model. Each now has its own dedicated View permission (Destination
        // Connections/Mapping Profiles/Transformation Rules were UnifiedAdmin-only until this change),
        // so the parent gate is an OR across all four children's View permissions — entering the shell
        // only requires being able to reach at least one tab, same reasoning as the Settings hub itself.
        path: 'workflow-configurations',
        canActivate: [permissionGuard],
        data: { permissions: ['sourceconnections.view', 'destinationconnections.view', 'mappingprofiles.view', 'transformationrules.view'] },
        loadComponent: () =>
          import('./layout/workflow-configurations-shell/workflow-configurations-shell.component').then(
            m => m.WorkflowConfigurationsShellComponent
          ),
        children: [
          {
            path: 'source-connections',
            canActivate: [permissionGuard],
            data: { permissions: ['sourceconnections.view'] },
            loadComponent: () =>
              import('../source-connections/pages/source-connection-list/source-connection-list.component').then(
                m => m.SourceConnectionListComponent
              ),
          },
          {
            path: 'destination-connections',
            canActivate: [permissionGuard],
            data: { permissions: ['destinationconnections.view'] },
            loadComponent: () =>
              import('../destination-connections/pages/destination-connection-list/destination-connection-list.component').then(
                m => m.DestinationConnectionListComponent
              ),
          },
          {
            path: 'mapping-profiles',
            canActivate: [permissionGuard],
            data: { permissions: ['mappingprofiles.view'] },
            loadComponent: () =>
              import('../mapping-profiles/pages/mapping-profile-list/mapping-profile-list.component').then(
                m => m.MappingProfileListComponent
              ),
          },
          {
            path: 'transformation-rules',
            canActivate: [permissionGuard],
            data: { permissions: ['transformationrules.view'] },
            loadComponent: () =>
              import('../transformation-rules/pages/transformation-rule-list/transformation-rule-list.component').then(
                m => m.TransformationRuleListComponent
              ),
          },
          // Was a static `redirectTo: 'source-connections'` — landed a Mapping-Profiles-only (or
          // Destination-Connections/Transformation-Rules-only) role on Source Connections, which
          // their permission doesn't cover, bouncing them straight to /unauthorized.
          {
            path: '',
            pathMatch: 'full',
            canActivate: [settingsLandingGuard('/settings/workflow-configurations', [
              { path: 'source-connections', permissions: ['sourceconnections.view'] },
              { path: 'destination-connections', permissions: ['destinationconnections.view'] },
              { path: 'mapping-profiles', permissions: ['mappingprofiles.view'] },
              { path: 'transformation-rules', permissions: ['transformationrules.view'] },
            ])],
          },
        ],
      },
      {
        path: 'allowed-origins',
        canActivate: [superAdminGuard],
        loadComponent: () =>
          import('../allowed-origins/pages/allowed-cors-origin-list/allowed-cors-origin-list.component').then(
            m => m.AllowedCorsOriginListComponent
          ),
      },
      {
        // Merges the formerly-standalone Email Settings, System Settings, and System Security tabs into
        // one screen with a section per former tab. General/Security remain SuperAdmin-role-only (they
        // have no permission of their own — AllowedCorsOriginsController/SystemSettingController/
        // AppSecretController are all AuthorizationPolicies.SuperAdminOnly on the backend), but Email and
        // the four Terminology Codes systems are independently permission-controlled (configuration.*/
        // loinc.*/snomedct.*/rxnorm.*/icd10.*) and must be reachable by a role that holds one of those
        // without also being SuperAdmin — so this parent gate is an OR across every one of them, exactly
        // like workflow-configurations above; General/Security get their OWN explicit superAdminGuard on
        // their child routes below rather than inheriting a blanket one from here.
        path: 'system-settings',
        canActivate: [permissionGuard],
        data: { permissions: SYSTEM_SETTINGS_PERMISSIONS },
        loadComponent: () =>
          import('./layout/system-settings-shell/system-settings-shell.component').then(
            m => m.SystemSettingsShellComponent
          ),
        children: [
          {
            path: 'email',
            canActivate: [permissionGuard],
            data: { permissions: ['configuration.view', 'configuration.write'] },
            canDeactivate: [unsavedChangesGuard],
            loadComponent: () =>
              import('./pages/email-settings/email-settings.component').then(m => m.EmailSettingsComponent),
          },
          {
            path: 'general',
            canActivate: [superAdminGuard],
            loadComponent: () =>
              import('../system-settings/pages/system-setting-list/system-setting-list.component').then(
                m => m.SystemSettingListComponent
              ),
          },
          {
            path: 'security',
            canActivate: [superAdminGuard],
            loadComponent: () =>
              import('../system-security/pages/app-secret-list/app-secret-list.component').then(
                m => m.AppSecretListComponent
              ),
          },
          {
            // Groups the four code-system import screens (LOINC's existing vendor sync plus SNOMED
            // CT/RxNorm/ICD-10 upload-based import) under one nested shell with a tab per system. Each
            // system is independently permission-controlled, so this parent gate is an OR across all
            // eight loinc/snomedct/rxnorm/icd10 view/write codes; each leaf route below is then gated
            // on its OWN specific system's codes only.
            path: 'terminology',
            canActivate: [permissionGuard],
            data: { permissions: TERMINOLOGY_PERMISSIONS },
            loadComponent: () =>
              import('./layout/terminology-configurations-shell/terminology-configurations-shell.component').then(
                m => m.TerminologyConfigurationsShellComponent
              ),
            children: [
              {
                path: 'loinc',
                canActivate: [permissionGuard],
                data: { permissions: ['loinc.view', 'loinc.write'] },
                loadComponent: () => import('./pages/loinc-settings/loinc-settings.component').then(m => m.LoincSettingsComponent),
              },
              {
                path: 'snomed-ct',
                canActivate: [permissionGuard],
                data: { permissions: ['snomedct.view', 'snomedct.write'] },
                loadComponent: () => import('./pages/snomed-settings/snomed-settings.component').then(m => m.SnomedSettingsComponent),
              },
              {
                path: 'rxnorm',
                canActivate: [permissionGuard],
                data: { permissions: ['rxnorm.view', 'rxnorm.write'] },
                loadComponent: () => import('./pages/rxnorm-settings/rxnorm-settings.component').then(m => m.RxnormSettingsComponent),
              },
              {
                path: 'icd-10',
                canActivate: [permissionGuard],
                data: { permissions: ['icd10.view', 'icd10.write'] },
                loadComponent: () => import('./pages/icd10-settings/icd10-settings.component').then(m => m.Icd10SettingsComponent),
              },
              // icd-10-pcs / hcpcs / ndc / cvx / ucum / cpt have no backend permission group yet (no
              // PermissionGroupCode member exists for any of them) — left ungated, matching how they
              // landed upstream, rather than inventing a permission code with nothing behind it.
              // Anyone who can reach the parent 'terminology' route (gated on TERMINOLOGY_PERMISSIONS
              // above) can open these; revisit once real RBAC coverage lands for them.
              { path: 'icd-10-pcs', loadComponent: () => import('./pages/icd10pcs-settings/icd10pcs-settings.component').then(m => m.Icd10PcsSettingsComponent) },
              { path: 'hcpcs', loadComponent: () => import('./pages/hcpcs-settings/hcpcs-settings.component').then(m => m.HcpcsSettingsComponent) },
              { path: 'ndc', loadComponent: () => import('./pages/ndc-settings/ndc-settings.component').then(m => m.NdcSettingsComponent) },
              { path: 'cvx', loadComponent: () => import('./pages/cvx-settings/cvx-settings.component').then(m => m.CvxSettingsComponent) },
              { path: 'ucum', loadComponent: () => import('./pages/ucum-settings/ucum-settings.component').then(m => m.UcumSettingsComponent) },
              { path: 'cpt', loadComponent: () => import('./pages/cpt-settings/cpt-settings.component').then(m => m.CptSettingsComponent) },
              // Was a static `redirectTo: 'loinc'` — landed a SNOMED/RxNorm/ICD-10-only role on LOINC's
              // own route, which their permissions don't cover, bouncing them straight to /unauthorized.
              {
                path: '',
                pathMatch: 'full',
                canActivate: [settingsLandingGuard('/settings/system-settings/terminology', [
                  { path: 'loinc', permissions: ['loinc.view', 'loinc.write'] },
                  { path: 'snomed-ct', permissions: ['snomedct.view', 'snomedct.write'] },
                  { path: 'rxnorm', permissions: ['rxnorm.view', 'rxnorm.write'] },
                  { path: 'icd-10', permissions: ['icd10.view', 'icd10.write'] },
                ])],
              },
            ],
          },
          {
            // SSO Configurations (SAML/magic-link admin screen) is SuperAdmin-role-only, matching the
            // backend's SsoConfigurationsController ([Authorize(Policy = SuperAdminOnly)]) — same
            // pattern as General/Security above, gated by role on its own child route rather than by
            // a permission code.
            path: 'sso-configurations',
            canActivate: [superAdminGuard],
            canDeactivate: [unsavedChangesGuard],
            loadComponent: () =>
              import('./pages/sso-configurations/sso-configurations.component').then(
                m => m.SsoConfigurationsComponent
              ),
          },
          // Was a static `redirectTo: 'email'` — landed a Terminology-Codes-only (or General/Security-
          // only) role on Email's own route, which their permissions/role don't cover, bouncing them
          // straight to /unauthorized instead of into the section they can actually use.
          {
            path: '',
            pathMatch: 'full',
            canActivate: [settingsLandingGuard('/settings/system-settings', [
              { path: 'email', permissions: ['configuration.view', 'configuration.write'] },
              { path: 'terminology', permissions: TERMINOLOGY_PERMISSIONS },
              { path: 'general', superAdminOnly: true },
              { path: 'security', superAdminOnly: true },
              { path: 'sso-configurations', superAdminOnly: true },
            ])],
          },
        ],
      },
      // Was a static `redirectTo: 'branding'` — landed every Settings sub-permission holder without
      // configuration.write (EHR Endpoints/Source Connections/.../Terminology Codes) on Branding, which
      // their permission doesn't cover, bouncing them straight to /unauthorized instead of the tab they
      // actually have access to.
      {
        path: '',
        pathMatch: 'full',
        canActivate: [settingsLandingGuard('/settings', [
          { path: 'branding', permissions: ['configuration.write'] },
          { path: 'workflow-configurations', permissions: ['sourceconnections.view', 'destinationconnections.view', 'mappingprofiles.view', 'transformationrules.view'] },
          { path: 'ehr-endpoints', permissions: ['ehrendpoints.view'] },
          { path: 'system-settings', permissions: SYSTEM_SETTINGS_PERMISSIONS },
          { path: 'allowed-origins', superAdminOnly: true },
        ])],
      },
    ],
  },
];
