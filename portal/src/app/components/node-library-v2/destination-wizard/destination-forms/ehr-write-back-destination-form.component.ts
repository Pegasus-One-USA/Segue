import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
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
  connectionsFor,
  defaultRunMode,
  ehrReviewLines,
  isEhrWriteVendor,
  isTestableVendor,
  runModeFields,
  runModeOf,
  runModesFor,
  vendorLabel,
} from './ehr-write-back/ehr-write-back.model';
import { EhrRunModePickerComponent } from './ehr-write-back/ehr-run-mode-picker.component';
import { EhrWriteConnectionPickerComponent, EhrWriteTargetsError } from './ehr-write-back/ehr-write-connection-picker.component';
import { EhrWriteGeneralOptionsComponent } from './ehr-write-back/ehr-write-general-options.component';
import { EhrEcwWriteOptionsComponent } from './ehr-write-back/ehr-ecw-write-options.component';
import { EhrAthenaWriteOptionsComponent } from './ehr-write-back/ehr-athena-write-options.component';
import { EhrCloneModeOptionComponent } from './ehr-write-back/ehr-clone-mode-option.component';

/** Why the chosen connection cannot be used (shown under the connection picker until another is chosen). */
type ConnectionNotice = 'droppedTestServer' | 'unavailable' | 'modeChanged' | 'createdNotWritable';

/**
 * EHR Write-Back destination form. The Node Library offers one tile per EHR (Epic, eClinicalWorks, athenahealth, FHIR
 * server); the tile presets `ehrVendor`, and only that vendor's write connections are offered (connections listed on
 * the Destination Connections page). A form opened without a tile (a node whose vendor cannot be recovered) offers
 * every write connection. There is no secret: the destination writes over the chosen connection's own credentials.
 *
 * Run mode replaces the old Dry run checkbox and "Test as" choice: Live writes into the vendor, Dry run (the default)
 * checks every record and sends nothing, and Test on a FHIR server sends exactly what the vendor would get to a
 * Generic FHIR test server instead. eClinicalWorks and athenahealth write through contracted / proprietary APIs, so
 * their types go live only once the connection says the practice has them activated; until then a Live run still
 * only checks and counts.
 *
 * With the EhrWriteBack:DryRunEnabled system setting off, a new destination is not offered Dry run: it starts as Test
 * on a FHIR server where the vendor has one, else with no Run mode chosen (never Live by default). A node saved as a
 * dry run stays one, with Dry run still offered and a note saying why, until the user picks another mode.
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
    EhrRunModePickerComponent,
    EhrWriteConnectionPickerComponent,
    EhrWriteGeneralOptionsComponent,
    EhrEcwWriteOptionsComponent,
    EhrAthenaWriteOptionsComponent,
    EhrCloneModeOptionComponent,
  ],
  styleUrls: ['../destination-wizard.component.scss'],
  template: `
    <form [formGroup]="form" class="dw-form" autocomplete="off">
      @if (section() === 'connection') {
        <div class="dw-grid-2">
          <div class="dw-field" [class.dw-field--error]="form.controls.name.invalid && form.controls.name.touched">
            <label class="dw-label" for="dw-ewb-name">Destination name <span class="dw-req">*</span></label>
            <input id="dw-ewb-name" class="dw-input" formControlName="name" placeholder="e.g. Epic write-back" />
            @if (form.controls.name.invalid && form.controls.name.touched) {
              <span class="dw-error">Destination name is required.</span>
            }
          </div>
        </div>

        <app-ehr-run-mode-picker
          [control]="form.controls.runMode"
          [vendor]="vendor()"
          [runMode]="runMode()"
          [dryRunOffered]="dryRunOffered()"
          [dryRunSettingOff]="!dryRunSettingEnabled()"
          [target]="effective()"
          [connectionName]="connectionName()" />

        <app-ehr-write-connection-picker
          [control]="form.controls.sourceConnectionId"
          [targets]="visibleTargets()"
          [loading]="loading()"
          [loadError]="loadError()"
          [vendor]="vendor()"
          [runMode]="runMode()"
          (created)="onCreated($event)" />

        @if (connectionNotice(); as notice) {
          <div class="dw-callout dw-callout--warn" data-testid="ewb-connection-notice">{{ noticeText(notice) }}</div>
        }

        @if (!connectionMismatch() && effective(); as target) {
          <div class="dw-callout dw-callout--info">
            {{ label(target.vendor) }} accepts writes for: {{ target.resourceTypes.join(', ') }}.
            @if (testAs() === 'Athenahealth') {
              athenaOne's REST calls are answered by a built-in athenaOne test server and filed on
              {{ selected()?.name }} as Basic resources (one per call, every field as sent); its allergen and medication
              lookups always find the name asked for. A test server must assign numeric ids, as HAPI does.
            } @else if (testAs()) {
              Patients are found on the test server by identifier only; tick "Create the patient" under Options to add
              the ones it lacks.
            }
          </div>
        }
      } @else {
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
  private readonly createdNotice = signal<'createdNotWritable' | null>(null);
  /** A reopened test run that was also a dry run: the test server it pointed at, which a plain Dry run cannot use. */
  private readonly droppedTestServerId = signal<string | null>(null);
  /** Without a tile: the testable vendor a reopened node was tested as, or the one chosen before switching to Test. */
  private readonly savedTestAs = signal<EhrWriteVendor | null>(null);
  /** The EhrWriteBack:DryRunEnabled setting, as the capabilities calls report it; on until they say otherwise. */
  readonly dryRunSettingEnabled = signal(false);
  /** The node was reopened as a dry run: it keeps Dry run on offer whatever the setting says. */
  private readonly savedDryRun = signal(false);
  private loadSubscription: Subscription | null = null;

  readonly form = this.fb.group({
    name: ['EHR write-back', [Validators.required]],
    sourceConnectionId: ['', [Validators.required]],
    maxWritesPerRun: ['500', [Validators.pattern(/^(?:[1-9]\d{0,3}|10000)$/)]],
    noteDocStatus: ['preliminary'],
    createPatientIfMissing: [false],
    cloneMode: [false],
    createHolderEncounter: [false],
    targetProviderId: ['', [Validators.pattern(/^\S{0,64}$/)]],
    targetDepartmentId: ['', [Validators.pattern(/^\S{0,64}$/)]],
    runMode: ['dryRun' as EhrRunMode | null],
  });

  /** The form's raw value as a signal, so what is derived from it recomputes as the user types. */
  private readonly formValue = toSignal(this.form.valueChanges.pipe(map(() => this.form.getRawValue())), {
    initialValue: this.form.getRawValue(),
  });
  /** Null while none is chosen: only Live is offered, and Live is never chosen for the user. */
  readonly runMode = computed<EhrRunMode | null>(() => this.formValue().runMode ?? null);

  readonly selected = computed(() => this.targets().find(t => t.id === this.formValue().sourceConnectionId) ?? null);

  /** Without a tile: the vendor a saved test run stood in for, else the chosen connection's own. */
  private readonly inferredVendor = computed<EhrWriteVendor | null>(() => {
    const saved = this.savedTestAs();
    if (saved) return saved;
    const vendor = this.selected()?.vendor;
    return isEhrWriteVendor(vendor) ? vendor : null;
  });
  /** The vendor written to, or stood in for in a test run. */
  readonly vendor = computed<EhrWriteVendor | null>(() => this.ehrVendor() ?? this.inferredVendor());
  /** Dry run is on offer: the setting is on, or the node was saved as a dry run. */
  readonly dryRunOffered = computed(() => this.dryRunSettingEnabled() || this.savedDryRun());
  /** The run modes offered for the vendor, as the picker shows them. */
  private readonly offeredModes = computed(() => runModesFor(this.vendor(), this.dryRunOffered()));
  /** The vendor a test run stands in for, '' when not testing. */
  readonly testAs = computed<string>(() => {
    const vendor = this.vendor();
    return this.runMode() === 'test' && isTestableVendor(vendor) ? vendor : '';
  });
  /** The connections that fit the vendor and run mode. With no vendor known, Live and Dry run list everything. */
  readonly visibleTargets = computed(() => connectionsFor(
    this.targets(),
    this.ehrVendor() ?? this.savedTestAs() ?? (this.runMode() === 'test' ? this.vendor() : null),
    this.runMode(),
  ));

  /**
   * One rule keeps the chosen connection consistent with the tile and the run mode: once the connections have loaded,
   * a chosen one that is not offered (run mode changed, tile switched, the saved one removed, disabled or read-only, or
   * a reopened test server that no longer fits) is reported here, and isValid() stays false until another is chosen.
   * The form value itself is never changed behind the user's back: the form also stays mounted, hidden, while a chain
   * node in front of the write-back is edited, and a save from there must not rewrite the destination's connection.
   * Nothing is reported while the list could not be loaded (no right to view it, or a failed request).
   */
  readonly connectionMismatch = computed<ConnectionNotice | null>(() => {
    if (this.loading() || this.loadError()) return null;
    const id = this.formValue().sourceConnectionId ?? '';
    if (!id || this.visibleTargets().some(t => t.id === id)) return null;
    if (id === this.droppedTestServerId()) return 'droppedTestServer';
    return this.targets().some(t => t.id === id) ? 'modeChanged' : 'unavailable';
  });
  /** What the picker says under itself: why the chosen connection cannot be used. */
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
    this.form.controls.sourceConnectionId.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(id => {
      this.createdNotice.set(null);
      if (id !== this.droppedTestServerId()) this.droppedTestServerId.set(null);
    });

    // Without a tile the vendor comes from the chosen connection, and a test run offers only FHIR servers, so picking
    // one would lose the vendor it stands in for. Switching to Test keeps the vendor chosen until then.
    this.form.controls.runMode.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(mode => {
      if (mode !== 'test' || this.ehrVendor() || this.savedTestAs()) return;
      const vendor = this.selected()?.vendor;
      if (isTestableVendor(vendor)) this.savedTestAs.set(vendor);
    });

    // A run mode that is not offered (its radio is not shown: the vendor has no Test, or Dry run is turned off) falls
    // back to Dry run, else Test on a FHIR server, else to no mode at all: never to Live, which the user must choose.
    // No mode chosen stays unchosen. Not while the list could not be loaded: then the vendor and the setting are not
    // known and nothing is changed.
    effect(() => {
      const mode = this.runMode();
      const offered = this.offeredModes();
      if (this.loading() || this.loadError() || mode === null || offered.includes(mode)) return;
      untracked(() => this.form.controls.runMode.setValue(defaultRunMode(offered)));
    });
  }

  /** A connection was created on the go: reload, and pick it when it fits. */
  onCreated(connection: SourceConnectionModel): void {
    this.loadTargets(() => {
      if (this.visibleTargets().some(t => t.id === connection.id)) {
        this.form.controls.sourceConnectionId.setValue(connection.id);
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

  noticeText(notice: ConnectionNotice): string {
    switch (notice) {
      case 'droppedTestServer':
        return `This destination used to dry-run against a FHIR test server. Choose a ${vendorLabel(this.vendor()) || 'write'} `
          + 'connection, or set Run mode to Test on a FHIR server.';
      case 'unavailable':
        return 'The saved connection is no longer available (removed, turned off, or read-only). Choose another.';
      case 'modeChanged':
        return 'That connection does not fit this run mode. Choose another.';
      case 'createdNotWritable':
        return 'The new connection was added but cannot take writes yet.';
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

  /** The connection half (Step 1): a name, an offered connection, a run mode this vendor has, and in a test run the
   *  tested vendor's capabilities loaded (Step 2 needs its resource types). */
  isValid(): boolean {
    const id = this.form.controls.sourceConnectionId.value;
    if (!this.form.controls.name.valid || !id || !this.visibleTargets().some(t => t.id === id)) return false;
    const mode = this.runMode();
    if (mode === null || !this.offeredModes().includes(mode)) return false;
    return mode !== 'test' || this.testTarget() !== null;
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
    const vendor = this.vendor();
    const mode = v.runMode ?? null;
    // Validity keeps an unfit test run (or no run mode at all) from being saved; this guard is only a safety net,
    // never a silent switch, and it falls to a dry run.
    const unfitTest = mode === 'test' && !(isTestableVendor(vendor) && this.selected()?.testableVendors.includes(vendor));
    const run = mode === null || unfitTest
      ? { dest_dryRun: 'true' as const, dest_testAsVendor: '' }
      : runModeFields(mode, vendor);
    return {
      dest_name: v.name ?? '',
      dest_sourceConnectionId: v.sourceConnectionId ?? '',
      // The tile's vendor when there is one (in a test run, the vendor stood in for), so a save made before the tested
      // vendor's capabilities load cannot record the test server's own vendor.
      dest_ehrVendor: this.ehrVendor() ?? (run.dest_testAsVendor || this.targetVendor()) ?? '',
      // Anything but an explicit false is a dry run, here and in the executor.
      dest_dryRun: run.dest_dryRun,
      dest_createPatientIfMissing: v.createPatientIfMissing ? 'true' : 'false',
      // Only ever true while the QA setting is on; the writer refuses clone mode otherwise.
      dest_cloneMode: v.cloneMode && this.selected()?.cloneModeEnabled ? 'true' : 'false',
      dest_maxWritesPerRun: v.maxWritesPerRun || '500',
      dest_noteDocStatus: v.noteDocStatus === 'final' ? 'final' : 'preliminary',
      // The vendor written as offers it (eCW, also when a test server stands in for it).
      dest_createHolderEncounter: v.createHolderEncounter && this.effective()?.offersHolderEncounter ? 'true' : 'false',
      dest_targetProviderId: (v.targetProviderId ?? '').trim(),
      dest_targetDepartmentId: (v.targetDepartmentId ?? '').trim(),
      dest_testAsVendor: run.dest_testAsVendor,
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
    const { mode, droppedTestServer } = runModeOf(fields);
    const testAs = fields['dest_testAsVendor'];
    this.savedTestAs.set(isTestableVendor(testAs) ? testAs : null);
    // Set before the patch, so a saved dry run is never seen as a mode the setting no longer offers.
    this.savedDryRun.set(mode === 'dryRun');
    this.form.patchValue({
      name: fields['dest_name'] || this.form.value.name || 'EHR write-back',
      sourceConnectionId: fields['dest_sourceConnectionId'] || '',
      maxWritesPerRun: fields['dest_maxWritesPerRun'] || '500',
      noteDocStatus: fields['dest_noteDocStatus'] === 'final' ? 'final' : 'preliminary',
      createPatientIfMissing: fields['dest_createPatientIfMissing'] === 'true',
      cloneMode: fields['dest_cloneMode'] === 'true',
      createHolderEncounter: fields['dest_createHolderEncounter'] === 'true',
      targetProviderId: fields['dest_targetProviderId'] ?? '',
      targetDepartmentId: fields['dest_targetDepartmentId'] ?? '',
      runMode: mode,
    });
    // Set after the patch, which clears it along with any other notice about an earlier connection.
    this.droppedTestServerId.set(droppedTestServer ? fields['dest_sourceConnectionId'] || null : null);
    this.createdNotice.set(null);
    this.enabledVariants.set((fields['dest_enabledVariants'] ?? '').split(',').map(v => v.trim()).filter(Boolean));
  }

  reset(): void {
    this.savedTestAs.set(null);
    this.savedDryRun.set(false);
    this.form.reset({
      name: 'EHR write-back',
      sourceConnectionId: '',
      maxWritesPerRun: '500',
      noteDocStatus: 'preliminary',
      createPatientIfMissing: false,
      cloneMode: false,
      createHolderEncounter: false,
      targetProviderId: '',
      targetDepartmentId: '',
      runMode: 'dryRun',
    });
    this.droppedTestServerId.set(null);
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
        onLoaded?.();
      },
      error: (err: HttpErrorResponse) => {
        this.targets.set([]);
        this.loadError.set(err?.status === 403 ? 'noViewPermission' : 'failed');
        this.loading.set(false);
      },
    });
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
