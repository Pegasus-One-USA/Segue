import { Component, Type, computed, effect, input, signal, untracked, viewChild } from '@angular/core';
import { NgComponentOutlet } from '@angular/common';
import { DestinationType } from '../../../destination-connections/models/destination-configuration.model';
import { DestinationConfigFormComponent } from '../../shared/config-form/config-form.contract';
import { DESTINATION_FORM_REGISTRY } from './destination-forms/destination-form.registry';
import { WizardDestinationFormApi } from './destination-forms/destination-form-api';

export type DestinationConnectionFormMode = 'create' | 'edit' | 'view';

/**
 * Hosts whichever DESTINATION_FORM_REGISTRY component matches `destType` — the standalone Destination
 * Connections admin screen's equivalent of DestinationWizardComponent's Step 1. Used to take a narrow
 * `destType: 'sql' | 'csv'` and duplicate the sql/csv form-building logic inline; now it's a thin
 * NgComponentOutlet host, same as the wizard, so both surfaces render the identical per-type component
 * instead of two hand-kept-in-sync copies. `destType` is now the real DestinationType (not the old lossy
 * 'sql'/'csv' collapse) — see destination-connection-dialog.component.ts's toFormType() callers, which
 * resolve the concrete type (SqlServer/MySql/PostgreSql/AzureSql/Csv/Sftp/Mongo/...) before passing it down,
 * now that the in-form "Database engine" dropdown is gone (engine is fixed by which registry component loads,
 * not chosen inside the form — see SqlFamilyDestinationFormComponent).
 */
@Component({
  selector: 'app-destination-connection-form',
  standalone: true,
  imports: [NgComponentOutlet],
  templateUrl: './destination-connection-form.component.html',
  styleUrls: ['./destination-wizard.component.scss', './destination-connection-form.component.scss'],
})
export class DestinationConnectionFormComponent {
  readonly destType = input.required<DestinationType>();
  readonly mode = input<DestinationConnectionFormMode>('create');
  /** dest_* keyed config bag to pre-populate the loaded form with, if any (see patchFrom on each
   *  destination-forms/ component). */
  readonly initialConfig = input<Record<string, string> | null>(null);

  readonly isReadOnly = computed(() => this.mode() === 'view');
  readonly formType = computed<Type<DestinationConfigFormComponent> | null>(() => DESTINATION_FORM_REGISTRY[this.destType()] ?? null);

  private readonly formOutlet = viewChild(NgComponentOutlet);
  private readonly _pendingPatch = signal<Record<string, string> | null>(null);

  constructor() {
    effect(() => {
      const config = this.initialConfig();
      untracked(() => { if (config) this._pendingPatch.set(config); });
    });
    // Flushes a queued initialConfig patch the moment the loaded form component actually exists — mirrors
    // DestinationWizardComponent's identical pattern (ngOnInit-style population can run before the outlet's
    // child does).
    effect(() => {
      const outlet = this.formOutlet();
      const pending = this._pendingPatch();
      if (!outlet || !pending) return;
      const form = outlet.componentInstance as WizardDestinationFormApi | null;
      if (!form) return;
      untracked(() => {
        form.patchFrom(pending);
        this._pendingPatch.set(null);
      });
    });
  }

  private activeForm(): WizardDestinationFormApi | null {
    return (this.formOutlet()?.componentInstance as WizardDestinationFormApi | null) ?? null;
  }

  isValid(): boolean {
    return this.activeForm()?.isValid() ?? false;
  }

  /** The non-secret fields + assembled secret for the current form, or null if invalid — replaces the old
   *  getConfig() + the dialog's own buildSqlConnectionString/buildSftpUri/buildConnectionMetadata calls,
   *  since every destination-forms/ component now assembles that itself (see e.g.
   *  SqlFamilyDestinationFormComponent.getMetadata()). */
  getMetadata(): { fields: Record<string, string>; secret?: string | null } | null {
    return this.activeForm()?.getMetadata() ?? null;
  }
}
