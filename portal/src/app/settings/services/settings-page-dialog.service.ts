import { Injectable, Type, inject } from '@angular/core';
import { DialogService } from '../../core/services/dialog.service';

/** Settings screens that are reachable as a launcher row on Settings → System Settings → General. */
export type SettingsPageDialogKey =
  | 'license'
  | 'ehr-endpoints'
  | 'allowed-origins'
  | 'email'
  | 'security'
  | 'sso-configurations';

/**
 * Opens an existing settings PAGE component as a full, edge-to-edge dialog.
 *
 * These three screens used to be their own routes/nav entries. They now appear as launcher rows on
 * General, and the unchanged page component is hosted in a dialog instead — so behaviour inside them is
 * identical to the old menu entry, only the way in changed.
 *
 * `fillContent` + `maximizable` is the same treatment Source and Destination Connections give their own
 * screens: these are dense, table- or card-heavy pages that a small centered card would clip.
 *
 * EHR Endpoints, Allowed Origins and Security open their own Add/Edit/Delete dialogs from inside here.
 * That is fine: DialogService renders a STACK (see its `stack` signal), so a child dialog layers over its
 * parent rather than replacing or fighting it.
 *
 * Email and SSO Configurations implement HasUnsavedChanges and used to rely on a `canDeactivate` route
 * guard. They keep that protection here without any change: DialogRef.attemptClose() checks
 * HasUnsavedChanges first and routes through the same shared "Discard changes?" prompt, so closing a
 * dirty form via ×/Esc/backdrop still asks before discarding.
 */
@Injectable({ providedIn: 'root' })
export class SettingsPageDialogService {
  private readonly dialog = inject(DialogService);

  open(key: SettingsPageDialogKey): void {
    // Lazily imported so a host that never opens one of these (the app shell, on every page load) doesn't
    // pull it into its own bundle — matching how the removed routes used to load them.
    void this.load(key).then(component => {
      this.dialog.open<unknown, undefined, unknown>(component, {
        fillContent: true,
        maximizable: true,
        disableClose: true,
      });
    });
  }

  private load(key: SettingsPageDialogKey): Promise<Type<unknown>> {
    switch (key) {
      case 'license':
        return import('../pages/license-settings/license-settings.component')
          .then(m => m.LicenseSettingsComponent as Type<unknown>);
      case 'ehr-endpoints':
        return import('../../ehr-endpoints/pages/ehr-endpoint-list/ehr-endpoint-list.component')
          .then(m => m.EhrEndpointListComponent as Type<unknown>);
      case 'allowed-origins':
        return import('../../allowed-origins/pages/allowed-cors-origin-list/allowed-cors-origin-list.component')
          .then(m => m.AllowedCorsOriginListComponent as Type<unknown>);
      case 'email':
        return import('../pages/email-settings/email-settings.component')
          .then(m => m.EmailSettingsComponent as Type<unknown>);
      case 'security':
        return import('../../system-security/pages/app-secret-list/app-secret-list.component')
          .then(m => m.AppSecretListComponent as Type<unknown>);
      case 'sso-configurations':
        return import('../pages/sso-configurations/sso-configurations.component')
          .then(m => m.SsoConfigurationsComponent as Type<unknown>);
    }
  }
}
