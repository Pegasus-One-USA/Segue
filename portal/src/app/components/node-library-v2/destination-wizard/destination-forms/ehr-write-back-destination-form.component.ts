import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { Subscription, forkJoin, map, of, switchMap } from 'rxjs';
import { buildConnectionMetadata } from '../../../../destination-connections/utils/destination-connection-secret.util';
import { ISourceConnectionService } from '../../../../source-connections/services/i-source-connection.service';
import { SourceConnectionModel } from '../../../../source-connections/models/source-connection.model';
import { EhrWriteCapabilitiesService, EhrWriteCapability } from '../../../../services/ehr-write-capabilities.service';
import { PermissionService } from '../../../../auth/services/permission.service';
import { EhrWriteBackFormApi } from './destination-form-api';
import { EhrWriteOptIns } from '../ehr-write-type-grid/ehr-write-type-grid.model';
import { EhrWriteKindSwitches, KIND_LABELS } from '../ehr-write-type-grid/ehr-write-kinds.model';
import {
  EhrRunMode,
  EhrWriteVendor,
  OptInApi,
  WritableTarget,
  connectionGroupsFor,
  ehrReviewLines,
  isEhrWriteVendor,
  isTestableVendor,
  vendorLabel,
} from './ehr-write-back/ehr-write-back.model';
import { EhrDryRunOptionComponent } from './ehr-write-back/ehr-dry-run-option.component';
import { EhrWriteConnectionPickerComponent, EhrWriteTargetsError } from './ehr-write-back/ehr-write-connection-picker.component';
import { EhrWriteGeneralOptionsComponent } from './ehr-write-back/ehr-write-general-options.component';
import { EhrEcwWriteOptionsComponent } from './ehr-write-back/ehr-ecw-write-options.component';
import { EhrAthenaWriteOptionsComponent } from './ehr-write-back/ehr-athena-write-options.component';
import { EhrCloneModeOptionComponent } from './ehr-write-back/ehr-clone-mode-option.component';

/** Why the chosen connection cannot be used (shown under the connection picker until another is chosen). */
type ConnectionNotice = 'unavailable' | 'otherVendor' | 'createdNotWritable' | 'createdChooseIt';

/**
 * EHR Write-Back destination form. The Node Library offers one tile per EHR (Epic, eClinicalWorks, athenahealth); the
 * tile presets `ehrVendor`. Step 1 is only the Connection: the EHR's own write connections and the FHIR test servers
 * that receive exactly what it would (both listed on Destination Connections). The connection chosen decides the run:
 * an EHR connection writes live, a test server is a test run (nothing reaches the EHR). Nothing is chosen for the user,
 * so a live run is always picked on purpose. A form opened without a tile (a node whose vendor cannot be recovered,
 * such as a saved plain FHIR server write-back) lists every write connection.
 *
 * Dry run (check every record, send nothing) is a checkbox under Options, offered while the EhrWriteBack:DryRunEnabled
 * system setting is on; a node saved as a dry run keeps it ticked, with a note, whatever the setting says.
 *
 * The destination name is not asked for up front: it takes the chosen connection's name, and Review lets the user
 * change it (nameControl). eClinicalWorks and athenahealth write through contracted / proprietary APIs, so their types
 * go live only once the connection says the practice has them activated; until then a live run still only checks
 * and counts.
 *
 * The wizard shows the form in two halves (`section`): the connection (Step 1) and the options (Step 3, after the
 * resource types). The options are only HOW records are written; WHAT is written (the resource types and, under each,
 * the kinds of record) is chosen on Step 2, and the wizard hands the kinds' switches to this form (setWriteKinds),
 * which saves them in dest_enabledVariants and dest_createHolderEncounter as before. Each visible part is its own
 * component; this host owns the FormGroup, the loaded connections and what is derived from them.
 */
@Component({
  selector: 'app-ehr-write-back-destination-form',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    EhrWriteConnectionPickerComponent,
    EhrDryRunOptionComponent,
    EhrWriteGeneralOptionsComponent,
    EhrEcwWriteOptionsComponent,
    EhrAthenaWriteOptionsComponent,
    EhrCloneModeOptionComponent,
  ],
  styleUrls: ['../destination-wizard.component.scss'],
  template: `
    <form [formGroup]="form" class="dw-form" autocomplete="off">
      @if (section() === 'connection') {
        <app-ehr-write-connection-picker
          [control]="form.controls.sourceConnectionId"
          [vendor]="listVendor()"
          [own]="groups().own"
          [testServers]="groups().testServers"
          [loading]="loading()"
          [loadError]="loadError()"
          (created)="onCreated($event)" />

        @if (connectionNotice(); as notice) {
          <div class="dw-callout dw-callout--warn" data-testid="ewb-connection-notice">{{ noticeText(notice) }}</div>
        } @else if (testAs()) {
          <span class="dw-hint" data-testid="ewb-test-run-line">Test run: nothing reaches {{ label(testAs()) }}.</span>
        } @else if (plainFhirServer()) {
          <span class="dw-hint" data-testid="ewb-plain-fhir-note">This destination writes plain FHIR to a FHIR server.
            New destinations use a FHIR server only as a test server for an EHR; this one keeps working as saved.</span>
        }
      } @else {
        @if (dryRunOffered()) {
          <app-ehr-dry-run-option [control]="form.controls.dryRun" [settingOff]="!dryRunSettingEnabled()" />
        }

        <app-ehr-write-general-options
          [maxWrites]="form.controls.maxWritesPerRun"
          [noteDocStatus]="form.controls.noteDocStatus"
          [createPatient]="form.controls.createPatientIfMissing" />

        @if (effective()?.vendor === 'Healow') {
          <app-ehr-ecw-write-options [providerId]="form.controls.targetProviderId" />
        }

        @if (effective()?.vendor === 'Athenahealth') {
          <app-ehr-athena-write-options
            [providerId]="form.controls.targetProviderId"
            [departmentId]="form.controls.targetDepartmentId"
            [connectionDepartmentId]="selected()?.departmentId ?? null"
            [runMode]="runMode()" />
        }

        @if (selected()?.cloneModeEnabled) {
          <app-ehr-clone-mode-option [control]="form.controls.cloneMode" />
        }
      }
    </form>
  `,
})
export class EhrWriteBackDestinationFormComponent implements EhrWriteBackFormApi {
  readonly kind = 'ehrWriteBack' as const;

  private readonly fb = inject(FormBuilder);
  private readonly sourceConnections = inject(ISourceConnectionService);
  private readonly capabilities = inject(EhrWriteCapabilitiesService);
  private readonly permissions = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);

  /** The vendor the Node Library tile presets; null when the form was opened without one. */
  readonly ehrVendor = input<EhrWriteVendor | null>(null);
  /** Which half the wizard shows: the connection (Step 1) or the options (Step 3). */
  readonly section = input<'connection' | 'options'>('connection');

  readonly loading = signal(true);
  readonly loadError = signal<EhrWriteTargetsError | null>(null);
  readonly targets = signal<WritableTarget[]>([]);
  /** A connection created on the go that turned out not to fit; cleared as soon as another connection is chosen. */
  private readonly createdNotice = signal<'createdNotWritable' | 'createdChooseIt' | null>(null);
  /** Without a tile: the testable vendor a reopened node was tested as. */
  private readonly savedTestAs = signal<EhrWriteVendor | null>(null);
  /** The EhrWriteBack:DryRunEnabled setting, as the capabilities calls report it; on until they say otherwise. */
  readonly dryRunSettingEnabled = signal(false);
  /** The node was reopened as a dry run: it keeps Dry run on offer whatever the setting says. */
  private readonly savedDryRun = signal(false);
  private loadSubscription: Subscription | null = null;
  /** A reopened node's saved name and connection, until the connections load and show whether the name was the
   *  automatic one (the connection's own name). */
  private savedName: { name: string; connectionId: string } | null = null;

  readonly form = this.fb.group({
    /** Empty: the chosen connection's name. Changed on Review. */
    name: ['', [Validators.maxLength(200)]],
    sourceConnectionId: ['', [Validators.required]],
    maxWritesPerRun: ['500', [Validators.pattern(/^(?:[1-9]\d{0,3}|10000)$/)]],
    noteDocStatus: ['preliminary'],
    createPatientIfMissing: [false],
    cloneMode: [false],
    createHolderEncounter: [false],
    targetProviderId: ['', [Validators.pattern(/^\S{0,64}$/)]],
    targetDepartmentId: ['', [Validators.pattern(/^\S{0,64}$/)]],
    dryRun: [false],
  });

  /** The form's raw value as a signal, so what is derived from it recomputes as the user types. */
  private readonly formValue = toSignal(this.form.valueChanges.pipe(map(() => this.form.getRawValue())), {
    initialValue: this.form.getRawValue(),
  });
  readonly selected = computed(() => this.targets().find(t => t.id === this.formValue().sourceConnectionId) ?? null);

  /** The EHR the dropdown is grouped for: the tile's, or without a tile the one a reopened test run stood in for. */
  readonly listVendor = computed<EhrWriteVendor | null>(() => this.ehrVendor() ?? this.savedTestAs());
  /** The vendor written to, or stood in for in a test run: without a tile, the chosen connection's own. */
  readonly vendor = computed<EhrWriteVendor | null>(() => {
    const listed = this.listVendor();
    if (listed) return listed;
    const own = this.selected()?.vendor;
    return isEhrWriteVendor(own) ? own : null;
  });
  /** The dropdown's two groups; without a vendor, every write connection in `own`. */
  readonly groups = computed(() => {
    const vendor = this.listVendor();
    return vendor ? connectionGroupsFor(this.targets(), vendor) : { own: this.targets(), testServers: [] as WritableTarget[] };
  });
  private readonly visibleTargets = computed(() => [...this.groups().own, ...this.groups().testServers]);
  /** Dry run is on offer: the setting is on, or the node was saved as a dry run. */
  readonly dryRunOffered = computed(() => this.dryRunSettingEnabled() || this.savedDryRun());
  /** The vendor a test server stands in for, '' when the chosen connection is not a test server for it. */
  readonly testAs = computed<string>(() => {
    const vendor = this.vendor();
    const chosen = this.selected();
    return chosen?.vendor === 'GenericFhir' && isTestableVendor(vendor) && chosen.testableVendors.includes(vendor) ? vendor : '';
  });
  /** How the destination runs, from the connection chosen and the Dry run option; null before a connection. */
  readonly runMode = computed<EhrRunMode | null>(() => {
    if (!this.selected()) return null;
    if (this.formValue().dryRun === true) return 'dryRun';
    return this.testAs() ? 'test' : 'live';
  });
  /** A saved plain FHIR server write-back (no tile offers one any more): it keeps working as saved. */
  readonly plainFhirServer = computed(() => !this.testAs() && this.selected()?.vendor === 'GenericFhir');

  /**
   * Once the connections have loaded, a chosen one that is not offered (tile switched, the saved one removed, disabled
   * or read-only) is reported here, and isValid() stays false until another is chosen. The form value itself is never
   * changed behind the user's back: the form also stays mounted, hidden, while a chain node in front of the write-back
   * is edited, and a save from there must not rewrite the destination's connection. Nothing is reported while the
   * list could not be loaded (no right to view it, or a failed request).
   */
  readonly connectionMismatch = computed<ConnectionNotice | null>(() => {
    if (this.loading() || this.loadError()) return null;
    const id = this.formValue().sourceConnectionId ?? '';
    if (!id || this.visibleTargets().some(t => t.id === id)) return null;
    return this.targets().some(t => t.id === id) ? 'otherVendor' : 'unavailable';
  });
  /** What shows under the picker: why the chosen connection cannot be used, or what became of a new one. */
  readonly connectionNotice = computed<ConnectionNotice | null>(() => this.connectionMismatch() ?? this.createdNotice());

  /** The tested vendor's capabilities over the test server: everything is live, nothing waits for activation. */
  private readonly testTarget = signal<WritableTarget | null>(null);
  /** What the destination writes as: the tested vendor in a test run, else the connection's own. */
  readonly effective = computed(() => (this.testAs() && this.testTarget()) || this.selected());
  readonly targetVendor = computed(() => this.effective()?.vendor ?? null);
  readonly writableResourceTypes = computed(() => this.effective()?.resourceTypes ?? []);
  /** dest_enabledVariants: the optional kinds ticked on Step 2 (setWriteKinds), or as saved. */
  readonly enabledVariants = signal<string[]>([]);

  readonly optIns = computed<EhrWriteOptIns>(() => ({
    enabledVariants: this.enabledVariants(),
    createHolderEncounter: this.formValue().createHolderEncounter === true,
    createPatientIfMissing: this.formValue().createPatientIfMissing === true,
  }));

  constructor() {
    this.loadTargets();

    // The tested vendor's own capabilities, whenever the test run (or the connection offering it) changes.
    effect(onCleanup => {
      const vendor = this.testAs();
      const offered = this.selected()?.testableVendors ?? [];
      if (!vendor || !offered.includes(vendor)) {
        this.testTarget.set(null);
        return;
      }

      const subscription = this.capabilities.forVendor(vendor).subscribe(result =>
        this.testTarget.set(this.toTarget(
          { id: '', name: vendor, sourceSystemType: vendor, vendorWriteApisActivated: true } as SourceConnectionModel,
          result.capabilities,
          false,
          [])));
      onCleanup(() => subscription.unsubscribe());
    });

    // A newly chosen connection replaces any notice about the one before it.
    this.form.controls.sourceConnectionId.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() =>
      this.createdNotice.set(null));

    // Dry run is not offered (the setting is off and the node was not saved as one): it cannot stay ticked. Not while
    // the list could not be loaded: then the setting is not known and nothing is changed.
    effect(() => {
      if (this.loading() || this.loadError() || this.dryRunOffered() || this.formValue().dryRun !== true) return;
      untracked(() => this.form.controls.dryRun.setValue(false));
    });
  }

  /** A connection was created on the go: reload it. A test server that fits is picked; an EHR connection is not, so a
   *  live run is always chosen on purpose. */
  onCreated(connection: SourceConnectionModel): void {
    this.loadTargets(() => {
      if (this.groups().testServers.some(t => t.id === connection.id)) {
        this.form.controls.sourceConnectionId.setValue(connection.id);
      } else if (this.visibleTargets().some(t => t.id === connection.id)) {
        this.createdNotice.set('createdChooseIt');
      } else {
        this.createdNotice.set('createdNotWritable');
      }
    });
  }

  label(vendor: string | null | undefined): string {
    return vendorLabel(vendor);
  }

  connectionName(): string | null {
    return this.selected()?.name ?? null;
  }

  /** The destination name, changed on Review; left empty it takes the chosen connection's name. */
  nameControl(): FormControl<string | null> {
    return this.form.controls.name;
  }

  /** The EHR a live run writes into, or null for a test run, a dry run, or a plain FHIR server. */
  liveEhrLabel(): string | null {
    return this.runMode() === 'live' && !this.plainFhirServer() && this.vendor() ? vendorLabel(this.vendor()) : null;
  }

  noticeText(notice: ConnectionNotice): string {
    const ehr = vendorLabel(this.vendor()) || 'the EHR';
    switch (notice) {
      case 'unavailable':
        return 'The saved connection is no longer available (removed, turned off, or read-only). Choose another.';
      case 'otherVendor':
        return `That connection does not write to ${ehr}. Choose another.`;
      case 'createdNotWritable':
        return 'The new connection was added but cannot take writes yet.';
      case 'createdChooseIt':
        return `The new connection was added. Choose it above to write into ${ehr}.`;
    }
  }

  /** The kinds of record chosen on Step 2, as the switches the writer reads: the variants turned on, and the holder
   *  encounter some history kinds are filed on. Saved as dest_enabledVariants and dest_createHolderEncounter. */
  setWriteKinds(kinds: EhrWriteKindSwitches): void {
    this.enabledVariants.set([...kinds.enabledVariants]);
    if (this.form.controls.createHolderEncounter.value !== kinds.createHolderEncounter) {
      this.form.controls.createHolderEncounter.setValue(kinds.createHolderEncounter);
    }
  }

  /** The connection half (Step 1): an offered connection, and for a test server the tested vendor's capabilities
   *  loaded (Step 2 needs its resource types). */
  isValid(): boolean {
    const id = this.form.controls.sourceConnectionId.value;
    if (!this.form.controls.name.valid || !id || !this.visibleTargets().some(t => t.id === id)) return false;
    return !this.testAs() || this.testTarget() !== null;
  }

  /** The options half (Step 3). */
  optionsValid(): boolean {
    const c = this.form.controls;
    return c.maxWritesPerRun.valid && c.targetProviderId.valid && c.targetDepartmentId.valid;
  }

  getRawValue(): Record<string, unknown> {
    return this.form.getRawValue();
  }

  getFullConfig(): Record<string, string> {
    const v = this.form.getRawValue();
    const testAs = this.testAs();
    return {
      dest_name: (v.name ?? '').trim() || this.selected()?.name || 'EHR write-back',
      dest_sourceConnectionId: v.sourceConnectionId ?? '',
      // The tile's vendor when there is one (for a test server, the vendor stood in for), so a save made before the
      // tested vendor's capabilities load cannot record the test server's own vendor.
      dest_ehrVendor: this.ehrVendor() ?? (testAs || this.targetVendor()) ?? '',
      // Anything but an explicit false is a dry run, here and in the executor.
      dest_dryRun: v.dryRun === true ? 'true' : 'false',
      dest_createPatientIfMissing: v.createPatientIfMissing ? 'true' : 'false',
      // Only ever true while the QA setting is on; the writer refuses clone mode otherwise.
      dest_cloneMode: v.cloneMode && this.selected()?.cloneModeEnabled ? 'true' : 'false',
      dest_maxWritesPerRun: v.maxWritesPerRun || '500',
      dest_noteDocStatus: v.noteDocStatus === 'final' ? 'final' : 'preliminary',
      // The vendor written as offers it (eCW, also when a test server stands in for it).
      dest_createHolderEncounter: v.createHolderEncounter && this.effective()?.offersHolderEncounter ? 'true' : 'false',
      dest_targetProviderId: (v.targetProviderId ?? '').trim(),
      dest_targetDepartmentId: (v.targetDepartmentId ?? '').trim(),
      dest_testAsVendor: testAs,
      // Only the variants the vendor written as offers; switching vendor drops the others.
      dest_enabledVariants: this.enabledVariants()
        .filter(variant => this.effective()?.optInApis.some(api => api.variant === variant))
        .join(','),
    };
  }

  /** The Review step's lines: where this destination writes and in which run mode. */
  reviewLines(): { writesTo: string; mode: string } {
    return ehrReviewLines(this.getFullConfig(), this.vendor(), this.connectionName());
  }

  getMetadata(): { fields: Record<string, string>; secret?: string | null } | null {
    if (!this.isValid() || !this.optionsValid()) return null;
    return {
      fields: JSON.parse(buildConnectionMetadata(this.getFullConfig(), 'ehrwriteback')) as Record<string, string>,
      secret: '',
    };
  }

  patchFrom(fields: Record<string, string>): void {
    const testAs = fields['dest_testAsVendor'];
    this.savedTestAs.set(isTestableVendor(testAs) ? testAs : null);
    // Anything but an explicit false is a dry run. Set before the patch, so a saved dry run stays ticked.
    const dryRun = fields['dest_dryRun'] !== 'false';
    this.savedDryRun.set(dryRun);
    this.form.patchValue({
      name: fields['dest_name'] ?? '',
      sourceConnectionId: fields['dest_sourceConnectionId'] || '',
      maxWritesPerRun: fields['dest_maxWritesPerRun'] || '500',
      noteDocStatus: fields['dest_noteDocStatus'] === 'final' ? 'final' : 'preliminary',
      createPatientIfMissing: fields['dest_createPatientIfMissing'] === 'true',
      cloneMode: fields['dest_cloneMode'] === 'true',
      createHolderEncounter: fields['dest_createHolderEncounter'] === 'true',
      targetProviderId: fields['dest_targetProviderId'] ?? '',
      targetDepartmentId: fields['dest_targetDepartmentId'] ?? '',
      dryRun,
    });
    this.savedName = { name: fields['dest_name'] ?? '', connectionId: fields['dest_sourceConnectionId'] || '' };
    this.resolveSavedName();
    // Set after the patch, which clears any notice about an earlier connection.
    this.createdNotice.set(null);
    this.enabledVariants.set((fields['dest_enabledVariants'] ?? '').split(',').map(v => v.trim()).filter(Boolean));
  }

  reset(): void {
    this.savedName = null;
    this.savedTestAs.set(null);
    this.savedDryRun.set(false);
    this.form.reset({
      name: '',
      sourceConnectionId: '',
      maxWritesPerRun: '500',
      noteDocStatus: 'preliminary',
      createPatientIfMissing: false,
      cloneMode: false,
      createHolderEncounter: false,
      targetProviderId: '',
      targetDepartmentId: '',
      dryRun: false,
    });
    this.createdNotice.set(null);
    this.enabledVariants.set([]);
  }

  /** Lists the enabled write connections with what each vendor accepts. Listing needs sourceconnections.view (the
   *  server checks it on GET source-connections), so without it nothing is asked and the picker says why. */
  private loadTargets(onLoaded?: () => void): void {
    this.loadSubscription?.unsubscribe();
    if (!this.permissions.hasPermission('sourceconnections.view')) {
      this.targets.set([]);
      this.loadError.set('noViewPermission');
      this.loading.set(false);
      return;
    }

    this.loading.set(true);
    this.loadError.set(null);
    // access=write asks the API for write-capable rows; the client-side check stays for an older API that ignores it.
    // Every capabilities answer carries the Dry run setting (off by default, like clone mode). Dry run is offered only
    // when every answer says it is on; a failed call or an older API omits it, which counts as off.
    this.loadSubscription = this.sourceConnections.getAll('write').pipe(
      map(connections => connections.filter(c => c.isEnabled && (c.access === 'Write' || c.access === 'ReadWrite'))),
      switchMap(connections => connections.length === 0
        ? of({ targets: [] as WritableTarget[], dryRunEnabled: false })
        : forkJoin(connections.map(c => this.capabilities.forVendor(c.sourceSystemType))).pipe(map(results => ({
            targets: results.map((result, i) =>
              this.toTarget(connections[i], result.capabilities, result.cloneModeEnabled, result.testableVendors)),
            dryRunEnabled: results.every(result => result.dryRunEnabled === true),
          })))),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: ({ targets, dryRunEnabled }) => {
        this.dryRunSettingEnabled.set(dryRunEnabled);
        this.targets.set(targets.filter(t => t.resourceTypes.length > 0));
        this.loading.set(false);
        this.resolveSavedName();
        onLoaded?.();
      },
      error: (err: HttpErrorResponse) => {
        this.targets.set([]);
        this.loadError.set(err?.status === 403 ? 'noViewPermission' : 'failed');
        this.loading.set(false);
      },
    });
  }

  /**
   * A saved name that is just the saved connection's own name (or the fallback used without one) was filled in
   * automatically, not typed: it is cleared, so the name follows the connection if another is chosen. A name the user
   * typed is kept. Runs once the connections are known; until then the saved name stays as it is.
   */
  private resolveSavedName(): void {
    const saved = this.savedName;
    if (!saved || this.loading()) return;
    this.savedName = null;
    const control = this.form.controls.name;
    if (control.dirty || (control.value ?? '') !== saved.name) return;
    const connectionName = this.targets().find(t => t.id === saved.connectionId)?.name ?? null;
    const automatic = saved.name.trim() === '' || saved.name === connectionName || saved.name === 'EHR write-back';
    if (automatic) control.setValue('');
  }

  private toTarget(
    connection: SourceConnectionModel,
    capabilities: EhrWriteCapability[],
    cloneModeEnabled: boolean,
    testableVendors: string[] | null | undefined,
  ): WritableTarget {
    const activated = connection.vendorWriteApisActivated === true;
    const distinct = (types: string[]) => [...new Set(types)];
    const optInApis: OptInApi[] = [];
    for (const capability of capabilities.filter(c => c.requiresVariantOptIn && c.variant)) {
      if (!optInApis.some(api => api.variant === capability.variant)) {
        optInApis.push({
          variant: capability.variant!,
          label: KIND_LABELS[capability.variant!] ?? capability.variant!,
          tabularOnly: capability.requiresTargetReferences === true,
        });
      }
    }

    return {
      id: connection.id,
      name: connection.name,
      vendor: connection.sourceSystemType,
      departmentId: connection.departmentId ?? null,
      resourceTypes: distinct(capabilities.map(c => c.resourceType)),
      liveTypes: distinct(capabilities
        .filter(c => c.liveWriteSupported && (!c.requiresVendorActivation || activated))
        .map(c => c.resourceType)),
      awaitingActivationTypes: distinct(capabilities
        .filter(c => c.liveWriteSupported && c.requiresVendorActivation && !activated)
        .map(c => c.resourceType)),
      offersHolderEncounter: capabilities.some(c => c.createsHolderEncounter === true),
      cloneModeEnabled,
      testableVendors: testableVendors ?? [],
      optInApis,
      capabilities,
    };
  }
}
