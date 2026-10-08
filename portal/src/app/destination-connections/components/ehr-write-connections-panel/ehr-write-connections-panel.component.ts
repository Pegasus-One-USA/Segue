import { Component, DestroyRef, OnDestroy, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HideWithoutPermissionDirective } from '../../../auth/directives/hide-without-permission.directive';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { EhrVendor } from '../../../ehr-endpoints/models/ehr-endpoint.model';
import { SOURCES } from '../../../data/sources.data';
import { WizardService } from '../../../services/wizard.service';
import { ISourceConnectionService } from '../../../source-connections/services/i-source-connection.service';
import { SourceConnectionModel } from '../../../source-connections/models/source-connection.model';
import { EpicSourceFormComponent } from '../../../components/node-library/epic-source-form/epic-source-form.component';
import { HealowSourceFormComponent } from '../../../components/node-library/healow-source-form/healow-source-form.component';
import { AthenahealthSourceFormComponent } from '../../../components/node-library/athenahealth-source-form/athenahealth-source-form.component';
import { GenericFhirWriteConnectionFormComponent } from '../generic-fhir-write-connection-form/generic-fhir-write-connection-form.component';

/** One "New" card: the vendor, its catalog id (abbreviation / brand colour) and its own permission prefix. */
interface WriteVendorCard {
  value: EhrVendor;
  label: string;
  sub: string;
  abbr: string;
  color: string;
  permissionPrefix: string;
}

/** The vendors an EHR write connection can be created for, in card order. Labels are what admins call them
 *  (eClinicalWorks, not the backend's Healow enum name). */
const WRITE_VENDORS: readonly { value: EhrVendor; sourceId: string; label: string; sub: string }[] = [
  { value: 'Epic', sourceId: 'epic', label: 'Epic', sub: 'Epic write-back over a Backend System app.' },
  { value: 'Healow', sourceId: 'healow', label: 'eClinicalWorks', sub: 'eClinicalWorks (Healow) FHIR write APIs.' },
  { value: 'Athenahealth', sourceId: 'athena', label: 'athenahealth', sub: 'athenaOne write APIs.' },
  { value: 'GenericFhir', sourceId: 'generic-fhir', label: 'FHIR server', sub: 'Plain FHIR R4, or a test server that receives what an EHR would.' },
];

const EHR_LABELS: Partial<Record<EhrVendor, string>> = Object.fromEntries(WRITE_VENDORS.map(v => [v.value, v.label]));

/** Vendors whose write-back types need contracted / proprietary APIs the practice must have activated. */
const ACTIVATION_VENDORS: ReadonlySet<EhrVendor> = new Set<EhrVendor>(['Healow', 'Athenahealth']);

/**
 * EHR write connections, on the Destination Connections page: the connections an EHR Write-Back destination writes
 * through. They are SourceConnection rows with write access (Write, or Read &amp; Write — those also appear under
 * Source Connections, as one record). Epic / eClinicalWorks / athenahealth open the same vendor form Source
 * Connections uses, in write purpose (Backend System only, Access = Write); a plain FHIR server has its own small
 * form. Visible with ehrwriteback.view plus sourceconnections.view (the list comes from GET source-connections, which
 * the server gates on sourceconnections.view). New needs ehrwriteback.create plus the vendor's create right (the server
 * checks both on a create with write access). Edit needs ehrwriteback.edit plus the vendor's edit right: the server
 * requires ehrwriteback.edit for any change to the write side of a connection that can write (base URL, credentials,
 * department, application type, access, activation), so the UI asks for it up front on every edit.
 */
@Component({
  selector: 'app-ehr-write-connections-panel',
  standalone: true,
  imports: [
    HideWithoutPermissionDirective,
    EpicSourceFormComponent,
    HealowSourceFormComponent,
    AthenahealthSourceFormComponent,
    GenericFhirWriteConnectionFormComponent,
  ],
  templateUrl: './ehr-write-connections-panel.component.html',
  styleUrl: './ehr-write-connections-panel.component.scss',
})
export class EhrWriteConnectionsPanelComponent implements OnDestroy {
  private readonly svc = inject(ISourceConnectionService);
  private readonly permissions = inject(PermissionService);
  private readonly actionGuard = inject(PermissionActionGuard);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly wiz = inject(WizardService);

  readonly connections = signal<SourceConnectionModel[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly pickerOpen = signal(false);
  readonly formMaximized = signal(false);
  /** The plain FHIR server form: 'new', the connection being viewed / edited, or null when closed. */
  readonly genericFhirEditing = signal<SourceConnectionModel | 'new' | null>(null);
  readonly genericFhirReadonly = signal(false);

  /** New cards the current role may use: ehrwriteback.create is checked on the New button itself, the vendor's own
   *  create right here (the server needs both). */
  readonly createCards = computed<WriteVendorCard[]>(() =>
    WRITE_VENDORS
      .map(v => {
        const entry = SOURCES.find(s => s.id === v.sourceId);
        return {
          value: v.value,
          label: v.label,
          sub: v.sub,
          abbr: entry?.abbr ?? v.label.slice(0, 2).toUpperCase(),
          color: entry?.color ?? 'var(--color-primary)',
          permissionPrefix: entry?.permissionPrefix ?? v.value.toLowerCase(),
        };
      })
      .filter(card => this.permissions.hasPermission(`${card.permissionPrefix}.create`)),
  );

  /** The vendor form modal is this panel's only while a write-purpose entity session is open (WizardService is a
   *  root singleton shared with Source Connections). */
  readonly vendorFormOpen = computed(
    () => this.wiz.isOpen() && this.wiz.wizardMode() === 'entity' && this.wiz.purpose() === 'write',
  );

  /** Both rights the panel needs: the write-back right, and the source-connections right the list endpoint checks.
   *  Without either the panel is hidden and never loads (a load would only fail with 403). */
  readonly viewCodes: readonly string[] = ['ehrwriteback.view', 'sourceconnections.view'];

  /** Baseline so the reload effect below fires only on a save made after this panel was created. */
  private readonly savedBaseline = this.wiz.saved();

  constructor() {
    effect(() => {
      if (this.wiz.saved() !== this.savedBaseline) this.load();
    });
    effect(() => {
      if (!this.wiz.isOpen()) this.formMaximized.set(false);
    });
    this.load();
  }

  /** WizardService outlives this panel: leaving the page mid-edit must not leave a write session open. */
  ngOnDestroy(): void {
    if (this.vendorFormOpen()) this.wiz.close();
  }

  load(): void {
    if (!this.permissions.hasAll(this.viewCodes)) return;
    this.loading.set(true);
    this.svc.getAll('write').pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: list => {
        this.loading.set(false);
        this.error.set(null);
        this.connections.set(Array.isArray(list) ? list : []);
      },
      error: () => {
        this.loading.set(false);
        this.connections.set([]);
        this.error.set('Loading EHR write connections failed. Try again.');
      },
    });
  }

  ehrLabel(c: SourceConnectionModel): string {
    return EHR_LABELS[c.sourceSystemType] ?? c.sourceSystemType;
  }

  /** "Activated" / "Not activated" for eCW and athena; "—" where the vendor has no activation-gated APIs. */
  activationLabel(c: SourceConnectionModel): string {
    if (!ACTIVATION_VENDORS.has(c.sourceSystemType)) return '—';
    return c.vendorWriteApisActivated ? 'Activated' : 'Not activated';
  }

  /** Both rights, not only the vendor's: the server requires ehrwriteback.edit for any change to the write side of a
   *  connection that can write (base URL, credentials, department, application type, access, activation), so the UI
   *  asks for it up front on every edit. */
  editCodes(c: SourceConnectionModel): string[] {
    return ['ehrwriteback.edit', `${c.sourceSystemType.toLowerCase()}.edit`];
  }

  openPicker(): void {
    this.pickerOpen.set(true);
  }

  closePicker(): void {
    this.pickerOpen.set(false);
  }

  onPickerBackdropClick(event: MouseEvent): void {
    if (event.target === event.currentTarget) this.closePicker();
  }

  chooseVendor(card: WriteVendorCard): void {
    if (!this.actionGuard.ensure(
      ['ehrwriteback.create', `${card.permissionPrefix}.create`],
      `You do not have permission to add a new ${card.label} write connection.`,
      'all',
    )) return;
    this.closePicker();
    if (card.value === 'GenericFhir') {
      this.genericFhirReadonly.set(false);
      this.genericFhirEditing.set('new');
      return;
    }
    // openEntity(null) seeds ehrType to Epic; set the chosen vendor right after, as Source Connections does.
    this.wiz.openEntity(null, { purpose: 'write' });
    this.wiz.ehrType.set(card.value);
  }

  openView(c: SourceConnectionModel): void {
    this.open(c, true);
  }

  /** No "used in a workflow" lock here (unlike Source Connections): a write connection in use must stay editable,
   *  e.g. to tick "Vendor write APIs activated" once the practice turns them on. That is safe for a Read & Write row a
   *  source workflow reads from because a write-purpose edit round-trips the saved retrieval and scopes (WizardService
   *  .save, GenericFhirWriteConnectionFormComponent.save) instead of clearing them. */
  openEdit(c: SourceConnectionModel): void {
    if (!this.actionGuard.ensure(this.editCodes(c), `You do not have permission to edit this ${this.ehrLabel(c)} write connection.`, 'all')) return;
    this.open(c, false);
  }

  private open(c: SourceConnectionModel, readonly: boolean): void {
    if (c.sourceSystemType === 'GenericFhir') {
      this.genericFhirReadonly.set(readonly);
      this.genericFhirEditing.set(c);
      return;
    }
    this.wiz.openEntity(c, { readonly, purpose: 'write' });
  }

  onVendorBackdropClick(event: MouseEvent): void {
    if (event.target === event.currentTarget) this.wiz.close();
  }

  genericFhirExisting(): SourceConnectionModel | null {
    const editing = this.genericFhirEditing();
    return editing === 'new' ? null : editing;
  }

  closeGenericFhir(): void {
    this.genericFhirEditing.set(null);
  }

  onGenericFhirSaved(): void {
    this.genericFhirEditing.set(null);
    this.load();
  }

  onGenericFhirBackdropClick(event: MouseEvent): void {
    if (event.target === event.currentTarget) this.closeGenericFhir();
  }
}
