import { Routes } from '@angular/router';
import { permissionGuard } from '../auth/guards/permission.guard';
import { superAdminGuard } from '../auth/guards/super-admin.guard';
import { roleGuard } from '../auth/guards/role.guard';
import { unsavedChangesGuard } from '../core/guards/unsaved-changes.guard';
import { settingsLandingGuard } from './guards/settings-landing.guard';
import { featureFlagGuard } from './guards/feature-flag.guard';
import { TERMINOLOGY_FEATURE_ENABLED, TERMINOLOGY_PERMISSION_CODES } from '../data/terminology-feature.config';

// Terminology Codes: each of the four import systems has its own independent View/Write pair
// (loinc.*/snomedct.*/rxnorm.*/icd10.*, split off from a shared "TerminologyCodes" group, itself
// originally split off from Email's configuration.view/write) — kept as one list (data/terminology-
// feature.config.ts) since every gate that needs "can this role reach ANY terminology system" (the
// shell route, its own landing redirect) uses the exact same OR across all eight codes.
const TERMINOLOGY_PERMISSIONS = TERMINOLOGY_PERMISSION_CODES;

// Email + Terminology Codes together — every permission that can unlock some part of the
// System Settings shell without the SuperAdmin role (General/Security stay role-only; see below).
// Terminology's codes only count toward this OR while the feature itself is enabled — otherwise a
// role holding only e.g. loinc.view would still see this shell's parent tab, then find every child
// inside it hidden (Email needs configuration.*, General/Security need SuperAdmin, and Terminology
// is force-disabled below), landing on an empty shell instead of being routed to something real.
const SYSTEM_SETTINGS_PERMISSIONS = ['configuration.view', 'configuration.write', ...(TERMINOLOGY_FEATURE_ENABLED ? TERMINOLOGY_PERMISSIONS : [])];

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
      // NOTE: 'ehr-endpoints' was removed — it is now a launcher row on System Settings > General, opened
      // as a full dialog. The row keeps the same ehrendpoints.view gate this route had, and General itself
      // is reachable with configuration.view/write, so a non-SuperAdmin holder still gets there.
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
            // The guard always returns a UrlTree (dynamic redirect), so children never render;
            // the empty `children` is only here to satisfy Angular's route-config validation
            // (a route needs one of component/loadComponent/redirectTo/children/loadChildren — a
            // canActivate alone throws NG04014).
            canActivate: [settingsLandingGuard('/settings/workflow-configurations', [
              { path: 'source-connections', permissions: ['sourceconnections.view'] },
              { path: 'destination-connections', permissions: ['destinationconnections.view'] },
              { path: 'mapping-profiles', permissions: ['mappingprofiles.view'] },
              { path: 'transformation-rules', permissions: ['transformationrules.view'] },
            ])],
            // canActivate always returns a redirect UrlTree — nothing ever renders here — but the
            // Angular Router still requires one of component/loadComponent/redirectTo/children/
            // loadChildren declared on every route (NG04014). An empty array satisfies that check
            // without changing behavior.
            children: [],
          },
        ],
      },
      // NOTE: 'allowed-origins' was removed — also a launcher row on System Settings > General now, still
      // gated superAdminOnly exactly as this route was, so nobody gained or lost access.
      // NOTE: the 'license' route was removed — License is now a launcher row on Settings > System Settings >
      // General, opened as a full dialog (SettingsPageDialogService). That row is gated superAdminOnly,
      // so License is SuperAdmin-only now; it was previously roleGuard ['SuperAdmin','Admin'], matching
      // LicenseController's UnifiedAdmin policy, which still accepts Admin. An Admin who is not a SuperAdmin
      // therefore no longer has any UI path to License, even though the API would still serve them.
      // The dev-mint route below deliberately keeps its own roleGuard and is still reachable directly.
      {
        // ⚠ TEMPORARY / DEV-ONLY — backs the "Dev: Mint a test license" page, linked from the License
        // settings screen's "Dev: Mint a test license →" button. Same roleGuard/roles as the 'license'
        // route above (already verified to match the backend's UnifiedAdmin policy) — the actual
        // security boundary is server-side (DevLicenseMintingController 404s outside Development), not
        // this route guard. Delete this route alongside license-dev-mint.component.* once minting moves
        // to its own separate internal tool.
        path: 'license/mint-dev',
        canActivate: [roleGuard],
        data: { roles: ['SuperAdmin', 'Admin'] },
        loadComponent: () =>
          import('./pages/license-dev-mint/license-dev-mint.component').then(
            m => m.LicenseDevMintComponent
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
        // like workflow-configurations above; Security gets its OWN explicit superAdminGuard on its child
        // route below rather than inheriting a blanket one from here. (General used to as well — it now
        // gates per row instead; see its child route.)
        path: 'system-settings',
        canActivate: [permissionGuard],
        // ehrendpoints.view is included because EHR Endpoints moved here from its own route: a role holding
        // only that permission must still get through this shell to reach General's EHR Endpoints row.
        // The General child re-checks configuration.* on its own, and each row gates itself, so widening
        // this parent OR does not expose anything further.
        data: { permissions: [...SYSTEM_SETTINGS_PERMISSIONS, 'ehrendpoints.view'] },
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
            // Opened up from superAdminGuard to the same permission pair Email uses: General now hosts
            // rows that a configuration.* holder legitimately manages (worker intervals, caching,
            // workflow numbering), plus rows that stay SuperAdmin-only. The page gates each row itself
            // (see SystemSettingListComponent's SUPER_ADMIN_ONLY_GROUPS / LAUNCHER_ROWS) rather than
            // locking the whole screen, so nothing that was SuperAdmin-only before became reachable.
            //
            // NOTE: this row-level split is UI-only. SystemSettingsController still authorizes every key
            // with the same configuration.* policy, so a caller holding configuration.write can still
            // change a locked key by calling the API directly. Making the restriction real needs
            // per-key gating server-side.
            path: 'general',
            canActivate: [permissionGuard],
            // ehrendpoints.view is in the OR because EHR Endpoints is a row on this page now: a role holding
            // only that permission has to reach General to open it. Such a role sees the EHR Endpoints row
            // and nothing else — the setting groups themselves still render only for configuration.* holders
            // (see SystemSettingListComponent.canSee / SUPER_ADMIN_ONLY_GROUPS).
            data: { permissions: ['configuration.view', 'configuration.write', 'ehrendpoints.view'] },
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
            // featureFlagGuard runs first: while TERMINOLOGY_FEATURE_ENABLED is false, this route (and
            // therefore every child under it — Angular never matches descendants of a blocked segment)
            // is unreachable for anyone, permissions notwithstanding, and a direct/bookmarked URL into
            // it redirects to the System Settings shell instead of rendering. See
            // data/terminology-feature.config.ts to re-enable.
            path: 'terminology',
            canActivate: [featureFlagGuard(TERMINOLOGY_FEATURE_ENABLED, '/settings/system-settings'), permissionGuard],
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
                // Empty children only to satisfy route-config validation; the guard always
                // redirects (UrlTree) so nothing renders here. See NG04014 note above.
                canActivate: [settingsLandingGuard('/settings/system-settings/terminology', [
                  { path: 'loinc', permissions: ['loinc.view', 'loinc.write'] },
                  { path: 'snomed-ct', permissions: ['snomedct.view', 'snomedct.write'] },
                  { path: 'rxnorm', permissions: ['rxnorm.view', 'rxnorm.write'] },
                  { path: 'icd-10', permissions: ['icd10.view', 'icd10.write'] },
                ])],
                children: [],
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
          // Every former sibling tab (Email, Security, SSO Configurations) is a launcher row on General
          // now, and Terminology Codes is feature-flagged off — so General is the only section left and a
          // permission-aware landing guard has nothing left to choose between. A plain redirect replaces
          // it. General's own child guard still decides whether this caller may proceed; restore
          // settingsLandingGuard here if a second section is ever added back.
          {
            path: '',
            pathMatch: 'full',
            redirectTo: 'general',
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
        // Empty children only to satisfy route-config validation; the guard always
        // redirects (UrlTree) so nothing renders here. See NG04014 note above.
        canActivate: [settingsLandingGuard('/settings', [
          { path: 'branding', permissions: ['configuration.write'] },
          { path: 'workflow-configurations', permissions: ['sourceconnections.view', 'destinationconnections.view', 'mappingprofiles.view', 'transformationrules.view'] },
          // ehr-endpoints / allowed-origins are no longer routes (they are launcher rows on
          // system-settings/general), so landing candidates point at system-settings instead — otherwise
          // this guard would redirect to a path that no longer resolves.
          { path: 'system-settings', permissions: [...SYSTEM_SETTINGS_PERMISSIONS, 'ehrendpoints.view'] },
          { path: 'system-settings', superAdminOnly: true },
        ])],
        children: [],
      },
    ],
  },
];
