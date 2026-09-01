import {
  Component,
  ElementRef,
  Injector,
  input,
  output,
  signal,
  computed,
  effect,
  Type,
  untracked,
  inject,
  viewChild,
  afterNextRender,
  OnInit,
} from '@angular/core';
import { NgComponentOutlet } from '@angular/common';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { forkJoin, of, Observable } from 'rxjs';
import {
  catchError,
  map,
  switchMap,
  finalize,
} from 'rxjs/operators';
import { CanvasNode } from '../../../models/node.model';
import { AddTransformEvent } from '../node-library-dialog.component';
import {
  DestinationSchemaService,
  DestinationTable,
  DestinationColumn,
  DestinationProbeRequest,
} from '../../../services/destination-schema.service';
import {
  MappingCatalogService,
  FhirElement,
} from '../../../services/mapping-catalog.service';
import { DestinationConfigurationService } from '../../../destination-connections/services/destination-configuration.service';
import {
  CreateDestinationConfigurationRequest,
  DestinationConfigurationDto,
  DestinationType,
  DeIdentificationProfileDto,
} from '../../../destination-connections/models/destination-configuration.model';
import { DeIdentificationProfileService } from '../../../destination-connections/services/deidentification-profile.service';
import {
  buildFhirSecretBlob,
  newSecretName,
} from '../../../destination-connections/utils/destination-connection-secret.util';
import { DestinationConfigFormComponent } from '../../shared/config-form/config-form.contract';
import { DESTINATION_FORM_REGISTRY } from './destination-forms/destination-form.registry';
import {
  WizardDestinationFormApi,
  SqlFamilyFormApi,
  isSqlFamilyForm,
  isMongoForm,
} from './destination-forms/destination-form-api';
import { FieldMappingCanvasComponent } from './field-mapping/field-mapping-canvas.component';
import {
  MappingRow,
  migrateLegacyRow,
  serializeRowsFlat,
  LegacyMappingRow,
  PendingSchemaOp,
  MappingDestType,
  qualifyTableName,
  reconcileTargetsForDestTypeSwitch,
  checkColumnTypeCompatibility,
} from './field-mapping/field-mapping-model';
import { computePendingTableNames, runQueuedOpsSequentially } from './field-mapping/field-mapping-schema-ops.util';
import { MappingSnapshotService } from './field-mapping/mapping-snapshot.service';
import {
  MappingSnapshot,
  MappingSnapshotSummary,
} from './field-mapping/mapping-snapshot.model';
import {
  MappingSummaryDocument,
  ChildTableRelation,
  buildMappingSummaryDocument,
  applyMappingSummaryDocument,
  pruneOrphanedMappingRows,
} from './field-mapping/field-mapping-summary.model';
import { MappingSummaryService } from './field-mapping/mapping-summary.service';
import { MappingProfileImportService } from './field-mapping/mapping-profile-import.service';
import { FieldMappingExportPreviewModalComponent } from './field-mapping/field-mapping-export-preview-modal.component';
import { DialogService } from '../../../core/services/dialog.service';
import {
  TransformRulesDialogComponent,
  TransformRulesDialogData,
} from './field-mapping/transform-rules-dialog/transform-rules-dialog.component';
import {
  TransformationRulesService,
  TransformationRule,
} from './field-mapping/transformation-rules.service';
import {
  ExistingMappingProfileDialogComponent,
  ExistingMappingProfileDialogData,
} from './field-mapping/existing-mapping-profile-dialog/existing-mapping-profile-dialog.component';
import {
  PromoteMappingProfileDialogComponent,
  PromoteMappingProfileDialogData,
} from './field-mapping/promote-mapping-profile-dialog/promote-mapping-profile-dialog.component';
import { MappingProfileService } from '../../../mapping-profiles/services/mapping-profile.service';
import {
  MappingProfileDto,
  MappingFieldDto,
  MappingValueType,
} from '../../../mapping-profiles/models/mapping-profile.model';
import {
  sortByDependencyRank,
  dependencyRankFor,
  recommendedFor,
  requiredClosureFor,
} from './resource-dependency.config';
import { ToastService } from '../../../services/toast.service';
import { SUPPORTED_RESOURCE_TYPES } from '../../../data/scope-constants.data';
import { PipelineStore } from '../../../services/pipeline.store';
import { EpicDiscoveryService } from '../../../services/epic-discovery.service';
import { ISourceConnectionService } from '../../../source-connections/services/i-source-connection.service';

// Matches Guid.Empty's JSON form — MappingImportService returns this as mappingProfileId when a resource's
// import fails (see ImportResourceMappingAsync's catch branch), alongside a warning explaining why.
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

// ── FHIR-repository (Aidbox) destination ──────────────────────────────────────
// This wizard's own destination-type union. MappingDestType (field-mapping-model.ts) covers only the
// destinations that have a field-by-field mapping canvas; 'fhir' and 'azurefhir' deliberately do NOT —
// both are FHIR-native, whole-resource passthrough destinations with no mapping canvas — so they're
// widened here rather than in MappingDestType, and nonFhirDestType() narrows back down at every call
// site that genuinely needs a mappable destination (guarded by isFhir()/isAzureFhir()).
export type WizardDestType = MappingDestType | 'fhir' | 'azurefhir';

/** All 20 transforms from Aidbox-Necessary-Transformations.md (Aidbox-Customize-Transform-UX-Plan.md's "All 20"
 *  section) — minus Redact/Rename, which stay the De-identification node's job (see that plan doc's Part 1c).
 *  codeLookup/statusCoercion (#7/#9) are mechanically identical (a source->target lookup table) but kept as two
 *  distinct entries for traceability back to the source doc's own numbering. #19/#20 (dateShift/hashMask)
 *  duplicate the standalone De-identification node's job — implemented here per explicit request, flagged as a
 *  known overlap in the plan doc, not silently resolved. */
export type FhirTransformType =
  | 'dateFormat'
  | 'typeCast'
  | 'booleanConversion'
  | 'unitConversion'
  | 'quantityRange'
  | 'roundPrecision'
  | 'codeLookup'
  | 'codeableConcept'
  | 'statusCoercion'
  | 'referenceConstruct'
  | 'telecom'
  | 'identifierFormat'
  | 'humanNameFormat'
  | 'addressParse'
  | 'stringClean'
  | 'stringTemplate'
  | 'arrayOp'
  | 'coalesce'
  | 'dateShift'
  | 'hashMask';

export interface FhirCustomRule {
  field: string;
  /** True when the user picked "Other field (advanced)" instead of the curated catalog below — reveals a
   *  free-text FHIRPath input rather than the friendly-label dropdown. */
  customField?: boolean;
  transform: FhirTransformType;
  params: Record<string, string>;
}

const FHIR_TRANSFORM_LABELS: Record<FhirTransformType, string> = {
  dateFormat: 'Date/time format',
  typeCast: 'Number / string cast',
  booleanConversion: 'Boolean conversion',
  unitConversion: 'Unit conversion (UCUM)',
  quantityRange: 'Quantity / Range assembly',
  roundPrecision: 'Rounding / scaling / precision',
  codeLookup: 'Value / code lookup',
  codeableConcept: 'CodeableConcept / Coding builder',
  statusCoercion: 'Status / enum coercion',
  referenceConstruct: 'Reference construct',
  telecom: 'Telecom (ContactPoint)',
  identifierFormat: 'Identifier formatting',
  humanNameFormat: 'HumanName parse',
  addressParse: 'Address parse',
  stringClean: 'String normalization / cleaning',
  stringTemplate: 'Concatenate / template / split',
  arrayOp: 'Array / list operation',
  coalesce: 'Default / coalesce',
  dateShift: 'Date math / date-shift',
  hashMask: 'Hashing / masking',
};

/** A rule's transform-specific parsed pair list for codeLookup/statusCoercion — stored as a JSON string in
 *  rule.params['pairs'] since params is a flat string map; parsed/serialized only for the sub-editor UI. */
export interface FhirLookupPair {
  from: string;
  to: string;
}

// ── Resource / field definitions (from HTML prototype) ─────────────────────────

export interface ResourceFieldDef {
  label: string;
  path: string;
  sqlColumn: string;
  csvColumn: string;
  // Array-aware metadata from the backend FHIR catalog (absent for the built-in fallback defs).
  jsonPath?: string;
  valueType?: string;
  arrays?: string[];
  // Resource types this field may reference (e.g. ["Patient", "Group"] for Observation.subject) — lets
  // the source tree surface "this could be the Patient link" without the user already knowing FHIR's
  // "subject"/"performer"/... naming for it. Absent for the built-in fallback defs, same as the above.
  referenceTargetTypes?: string[];
}

/** One unresolved "required parent reference" for the pendingSaveWarnings confirm-before-save dialog —
 *  see buildParentReferenceWarnings. `message` is a plain-text fallback for a case with no auto-fix
 *  (parent not selected as a data group, or the catalog has no reference field for it at all); when
 *  `sourceFieldPath` is set instead, the dialog offers a destination-column dropdown and a one-click fix
 *  (resolvePendingReferenceWarning) instead of just describing the problem. */
export interface PendingParentReferenceWarning {
  resource: string;
  parent: string;
  message: string | null;
  sourceFieldPath: string | null;
  sourceFieldLabel: string | null;
  sourceFieldJsonPath?: string;
  sourceFieldValueType?: string;
  sourceFieldArrays?: string[];
  /** Set when sourceFieldPath is already mapped to some column — the fix re-targets/resolves that row
   *  instead of creating a new one. */
  existingRow: MappingRow | null;
  destinationColumns: string[];
  selectedDestinationColumn: string | null;
}

/** One transform-rule/destination-column type mismatch blocking Save — see validateRuleConflictsForSave.
 *  `rule` is the full effective rule as currently resolved (Global/DestinationType/ResourceType/Field tier;
 *  never itself a Workflow-scoped row, since that would already have been the effective rule and wouldn't
 *  conflict). The dialog's fixes never touch `rule` — they create a NEW Workflow-scoped row that outranks
 *  it for this workflow only (see resolveRuleConflictOverride/resolveRuleConflictBypass). */
export interface PendingTransformRuleConflict {
  resource: string;
  tableName: string;
  targetName: string;
  destinationType: DestinationType;
  columnValueType: string;
  columnDataType: string;
  sourceSystem: string | null;
  sourceField: string | null;
  rule: TransformationRule;
  message: string;
}

export interface ResourceDef {
  scope: string;
  sqlTable: string;
  csvFile: string;
  fields: ResourceFieldDef[];
}

export const DEST_RESOURCE_DEFS: Record<string, ResourceDef> = {
  Patient: {
    scope: 'user/Patient.read',
    sqlTable: 'dbo.Patient',
    csvFile: 'patients.csv',
    fields: [
      {
        label: 'Patient ID',
        path: 'Patient.id',
        sqlColumn: 'SourcePatientId',
        csvColumn: 'PatientId',
      },
      {
        label: 'First Name',
        path: 'Patient.name.given',
        sqlColumn: 'FirstName',
        csvColumn: 'FirstName',
      },
      {
        label: 'Last Name',
        path: 'Patient.name.family',
        sqlColumn: 'LastName',
        csvColumn: 'LastName',
      },
      {
        label: 'Date of Birth',
        path: 'Patient.birthDate',
        sqlColumn: 'DateOfBirth',
        csvColumn: 'DateOfBirth',
      },
      {
        label: 'Gender',
        path: 'Patient.gender',
        sqlColumn: 'Gender',
        csvColumn: 'Gender',
      },
      {
        label: 'Phone',
        path: 'Patient.telecom.value',
        sqlColumn: 'Phone',
        csvColumn: 'Phone',
      },
    ],
  },
  Observation: {
    scope: 'user/Observation.read',
    sqlTable: 'dbo.PatientObservation',
    csvFile: 'observations.csv',
    fields: [
      {
        label: 'Observation ID',
        path: 'Observation.id',
        sqlColumn: 'SourceObservationId',
        csvColumn: 'ObservationId',
      },
      {
        label: 'Code',
        path: 'Observation.code.coding.code',
        sqlColumn: 'Code',
        csvColumn: 'Code',
      },
      {
        label: 'Display',
        path: 'Observation.code.coding.display',
        sqlColumn: 'Display',
        csvColumn: 'Display',
      },
      {
        label: 'Value',
        path: 'Observation.valueQuantity.value',
        sqlColumn: 'Value',
        csvColumn: 'Value',
      },
      {
        label: 'Unit',
        path: 'Observation.valueQuantity.unit',
        sqlColumn: 'Unit',
        csvColumn: 'Unit',
      },
      {
        label: 'Observed At',
        path: 'Observation.effectiveDateTime',
        sqlColumn: 'ObservedAt',
        csvColumn: 'ObservedAt',
      },
    ],
  },
  Encounter: {
    scope: 'user/Encounter.read',
    sqlTable: 'dbo.Encounter',
    csvFile: 'encounters.csv',
    fields: [
      {
        label: 'Encounter ID',
        path: 'Encounter.id',
        sqlColumn: 'SourceEncounterId',
        csvColumn: 'EncounterId',
      },
      {
        label: 'Status',
        path: 'Encounter.status',
        sqlColumn: 'Status',
        csvColumn: 'Status',
      },
      {
        label: 'Class',
        path: 'Encounter.class.code',
        sqlColumn: 'ClassCode',
        csvColumn: 'ClassCode',
      },
      {
        label: 'Patient Reference',
        path: 'Encounter.subject.reference',
        sqlColumn: 'PatientReference',
        csvColumn: 'PatientReference',
      },
      {
        label: 'Start',
        path: 'Encounter.period.start',
        sqlColumn: 'StartDate',
        csvColumn: 'StartDate',
      },
      {
        label: 'End',
        path: 'Encounter.period.end',
        sqlColumn: 'EndDate',
        csvColumn: 'EndDate',
      },
    ],
  },
  Condition: {
    scope: 'user/Condition.read',
    sqlTable: 'dbo.Condition',
    csvFile: 'conditions.csv',
    fields: [
      {
        label: 'Condition ID',
        path: 'Condition.id',
        sqlColumn: 'SourceConditionId',
        csvColumn: 'ConditionId',
      },
      {
        label: 'Code',
        path: 'Condition.code.coding.code',
        sqlColumn: 'Code',
        csvColumn: 'Code',
      },
      {
        label: 'Display',
        path: 'Condition.code.coding.display',
        sqlColumn: 'Display',
        csvColumn: 'Display',
      },
      {
        label: 'Clinical Status',
        path: 'Condition.clinicalStatus.coding.code',
        sqlColumn: 'ClinicalStatus',
        csvColumn: 'ClinicalStatus',
      },
      {
        label: 'Onset Date',
        path: 'Condition.onsetDateTime',
        sqlColumn: 'OnsetDate',
        csvColumn: 'OnsetDate',
      },
    ],
  },
  MedicationRequest: {
    scope: 'user/MedicationRequest.read',
    sqlTable: 'dbo.MedicationRequest',
    csvFile: 'medication_requests.csv',
    fields: [
      {
        label: 'Medication ID',
        path: 'MedicationRequest.id',
        sqlColumn: 'SourceMedicationRequestId',
        csvColumn: 'MedicationRequestId',
      },
      {
        label: 'Status',
        path: 'MedicationRequest.status',
        sqlColumn: 'Status',
        csvColumn: 'Status',
      },
      {
        label: 'Intent',
        path: 'MedicationRequest.intent',
        sqlColumn: 'Intent',
        csvColumn: 'Intent',
      },
      {
        label: 'Medication Code',
        path: 'MedicationRequest.medicationCodeableConcept.coding.code',
        sqlColumn: 'MedicationCode',
        csvColumn: 'MedicationCode',
      },
      {
        label: 'Requester',
        path: 'MedicationRequest.requester.reference',
        sqlColumn: 'RequesterReference',
        csvColumn: 'RequesterReference',
      },
    ],
  },
};

/** Field set for a resource not in DEST_RESOURCE_DEFS, so any source-selected resource stays mappable. */
function genericResourceDef(r: string): ResourceDef {
  return {
    scope: `user/${r}.read`,
    sqlTable: `dbo.${r}`,
    csvFile: `${r.toLowerCase()}.csv`,
    fields: [
      {
        label: `${r} ID`,
        path: `${r}.id`,
        sqlColumn: `Source${r}Id`,
        csvColumn: `${r}Id`,
      },
      {
        label: 'Status',
        path: `${r}.status`,
        sqlColumn: 'Status',
        csvColumn: 'Status',
      },
      {
        label: 'Subject',
        path: `${r}.subject.reference`,
        sqlColumn: 'SubjectReference',
        csvColumn: 'SubjectReference',
      },
    ],
  };
}

// ── Component ──────────────────────────────────────────────────────────────────

@Component({
  selector: 'app-destination-wizard',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    NgComponentOutlet,
    FieldMappingCanvasComponent,
    FieldMappingExportPreviewModalComponent,
  ],
  templateUrl: './destination-wizard.component.html',
  styleUrl: './destination-wizard.component.scss',
})
export class DestinationWizardComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly schemaSvc = inject(DestinationSchemaService);
  private readonly catalogSvc = inject(MappingCatalogService);
  private readonly toast = inject(ToastService);
  private readonly destinationConfigSvc = inject(
    DestinationConfigurationService,
  );
  private readonly deIdentificationProfileSvc = inject(DeIdentificationProfileService);
  private readonly mappingSnapshotSvc = inject(MappingSnapshotService);
  private readonly mappingSummarySvc = inject(MappingSummaryService);
  private readonly mappingProfileImportSvc = inject(
    MappingProfileImportService,
  );
  private readonly mappingProfileSvc = inject(MappingProfileService);
  private readonly pipelineStore = inject(PipelineStore);
  private readonly injector = inject(Injector);
  private readonly dialogService = inject(DialogService);
  private readonly transformationRulesSvc = inject(TransformationRulesService);
  private readonly discoverySvc = inject(EpicDiscoveryService);
  private readonly sourceConnectionSvc = inject(ISourceConnectionService);

  // Feature flag: Settings > System Settings > General, "TransformationRules:Hidden" (default false —
  // visible unless an admin explicitly hides it). Starts matching that default until the real value comes
  // back, so there's no flash on first paint in the common case.
  readonly rulesHidden = signal(false);

  // True while "Add to Pipeline"/"Update" is waiting on POST mapping-profiles/import.
  readonly savingMappingProfiles = signal(false);

  // Backend FHIR catalog fields per resource type (array-aware paths). Empty until fetched; the
  // built-in DEST_RESOURCE_DEFS act as the fallback when a resource isn't (yet) loaded.
  private readonly catalogByResource = signal<
    Record<string, ResourceFieldDef[]>
  >({});

  // Fields derived from a real FHIR JSON payload the user pasted via the canvas's "Load JSON payload"
  // affordance — takes priority over both the backend catalog and the built-in fallback for that
  // resource, since it mirrors data the user actually has rather than a generic field list.
  private readonly payloadFieldsByResource = signal<
    Record<string, ResourceFieldDef[]>
  >({});

  readonly destType = input.required<WizardDestType>();
  readonly attachNode = input.required<CanvasNode>();
  readonly editNode = input<CanvasNode | null>(null);
  /** FHIR resource types the upstream source is configured to pull — drives the data-group list (Step 2). */
  readonly sourceResources = input<string[]>([]);
  /** The upstream source's EHR vendor (e.g. "Epic") — the Mapping JSON's top-level "source" field. */
  readonly sourceVendor = input<string>('');
  /** The pipeline's launch source node's saved connection id — the Mapping JSON's per-resource
   *  "sourceConnectionId" field. Null if no source is wired up yet. */
  readonly sourceConnectionId = input<string | null>(null);
  /** FHIR resource types the source's live Discover (/metadata) probe actually returned THIS canvas
   *  session (see EhrVendorSourceFormComponent's 'Discovered resource types' field) — the preferred source
   *  for availableGroups' intersection filter below, since it needs no extra network round trip and is
   *  available even before the source has a real sourceConnectionId. Empty when Discover hasn't run this
   *  session, in which case the sourceConnectionId effect below falls back to a live re-probe. */
  readonly sourceDiscoveredResourceTypes = input<string[]>([]);
  /** The persisted ResourcePipelineRoute/workflow GUID when editing an already-saved workflow — null while
   *  still building a brand-new one (see WorkflowBuilderComponent.currentWorkflowId). Threaded through so a
   *  transform-rule-conflict override can be scoped to just this workflow (see validateRuleConflictsForSave /
   *  resolveRuleConflictOverride) — undefined/null means no workflow exists yet to scope an override to. */
  readonly currentWorkflowId = input<string | null>(null);
  // Incrementing counters from the parent's header-level Close/Save buttons (shown there instead of
  // the × while a group's mapping canvas is open) — any change triggers the matching action here.
  readonly exitMappingRequest = input<number>(0);
  readonly saveMappingRequest = input<number>(0);
  // Independent of the two above: saves/restores ONLY the mapping-screen state (see MappingSnapshot) —
  // never touches the workflow-level `saved` output, PipelineStore, or node.fields at all.
  readonly saveSnapshotRequest = input<number>(0);
  // null until each effect below has observed a real (post-input-binding) value — these counters live on
  // an ancestor (NodeLibraryDialogComponent) that outlives this wizard and never resets, so a freshly
  // (re)opened wizard must learn its baseline from whatever the counter already is, not assume 0, or it
  // mistakes an old, already-handled click (e.g. Close from a previous time this destination node was
  // configured) for a fresh one and fires the action with no user input this time — see the exact same
  // fix in FieldMappingCanvasComponent for openLoadPayloadRequest/openPreviewRequest.
  private _lastExitTrigger: number | null = null;
  private _lastSaveTrigger: number | null = null;
  private _lastSaveSnapshotTrigger: number | null = null;
  // Same pattern, passed straight through to the mapping canvas — its "Load JSON payload"/"Preview
  // output" actions now live in the dialog header (see NodeLibraryDialogComponent), not this canvas's
  // own toolbar, so the wizard just forwards these without reacting to them itself.
  readonly openLoadPayloadRequest = input<number>(0);
  readonly openPreviewRequest = input<number>(0);
  // Same forward-only pattern for the "Suggest mappings"/"Clear suggestions" buttons and the zoom-dock
  // controls, also relocated to the dialog header.
  readonly runSuggestMappingsRequest = input<number>(0);
  readonly clearSuggestionsRequest = input<number>(0);
  readonly zoomInRequest = input<number>(0);
  readonly zoomOutRequest = input<number>(0);
  readonly zoomResetRequest = input<number>(0);
  readonly zoomFitRequest = input<number>(0);
  // "Mark as Master" — unlike the forward-only triggers above, this wizard reacts to it directly (same
  // baseline-learning pattern as exitMappingRequest/saveMappingRequest): it needs the currently-open
  // resource's node/profile context, which only this component has.
  readonly markAsMasterRequest = input<number>(0);
  private _lastMarkAsMasterTrigger: number | null = null;
  /** Toolbar-level search (dialog header) — forwarded straight through to the canvas, which applies it
   *  to both the payload tree and every destination table's columns. Not a "request" counter like the
   *  actions above: this is live text, re-forwarded on every change rather than reacted to once here. */
  readonly mappingSearchQuery = input<string>('');
  /** Mirrors the canvas's own suggestionCountChange/zoomPercentChange straight up to the dialog header,
   *  which renders the "Clear N suggestions" label and zoom-percent readout. */
  readonly suggestionCountChange = output<number>();
  readonly zoomPercentChange = output<string>();
  /** Whether the OUTER Node Library dialog is currently maximized — this wizard's own topbar (see
   *  .dw-topbar) renders the maximize/restore button itself while its steps are showing, since the
   *  outer dialog's own header row is hidden then (see NodeLibraryDialogComponent's .nld-header) to
   *  avoid two title bars stacked on top of each other. */
  readonly isMaximized = input<boolean>(false);

  readonly saved = output<AddTransformEvent>();
  readonly cancelled = output<void>();
  readonly closeAll = output<void>();
  readonly toggleMaximizeRequest = output<void>();
  /** Total field-mapping count for the currently open group — the dialog header shows it next to the title. */
  readonly mappingCountChange = output<number>();

  // Lets the parent (Node Library sidebar) lock out the other destination type
  // mid-wizard, and warn before discarding progress if the user switches anyway.
  readonly stepChange = output<number>();
  readonly progressChange = output<boolean>();

  // Lets the parent hide its own sidebar while a specific group's mapping canvas is open, so the
  // canvas gets the full dialog width instead of sharing it with the node picker rail.
  readonly mappingCanvasActive = output<boolean>();
  // Lets the parent show "Map fields — {group}" in its own header in place of "Node Library" while
  // the canvas is open — null means "show your normal title", since no group is active.
  readonly mappingCanvasTitle = output<string | null>();

  // ── step state ────────────────────────────────────────────────────────────
  readonly step = signal(1);
  readonly TOTAL_STEPS = 4;
  readonly STEP_LABELS = ['Configure', 'Data groups', 'Map fields', 'Review'];

  // True once the user has advanced past Configure at least once this session —
  // stays true even after going back to step 1, so switching still warns.
  private readonly _hasProgressed = signal(false);

  // ── connection form (Step 1) ─────────────────────────────────────────────
  // The old inline sqlForm/csvForm/mongoForm reactive forms are gone — Step 1 now hosts whichever
  // DESTINATION_FORM_REGISTRY component matches this wizard's destType() via NgComponentOutlet, and every
  // downstream read (validity, review display, provisioning, save, existing-connection diffing, the mapping
  // canvas's connectionInfo/csvDelimiterKey) goes through activeForm()/activeFormConfig() instead of a class
  // field FormGroup. The outlet stays mounted for the wizard's whole lifetime (see the template's dw-hidden
  // toggle) so those reads keep working after Step 1 is behind — exactly like the old FormGroup fields did
  // just by existing on this class the whole time.
  private readonly formOutlet = viewChild(NgComponentOutlet);

  /** 'sql' is this wizard's existing shorthand specifically for SQL Server (see isSql()/isMySql()/isPostgres()
   *  below) — mapped to the registry's 'SqlServer' key. Mongo/csv/mysql/postgres map 1:1 onto their
   *  DestinationType names. */
  private readonly registryKey = computed<DestinationType>(() => {
    switch (this.destType()) {
      case 'mysql':
        return 'MySql';
      case 'postgres':
        return 'PostgreSql';
      case 'mongo':
        return 'Mongo';
      case 'csv':
        return 'Csv';
      case 'blob':
        return 'BlobStorage';
      case 'medplum':
        return 'Medplum';
      case 'azurefhir':
        return 'AzureFhirService';
      // 'fhir' (Aidbox) deliberately has no case here — it's never registry-routed (see isFhir()'s doc
      // comment on the already-shipped, live-verified hand-rolled Aidbox form/wizard steps). Falling through
      // to the default is harmless because activeFormType()/activeForm() are never consulted for 'fhir' —
      // every call site branches on isFhir() first.
      default:
        return 'SqlServer';
    }
  });

  readonly activeFormType =
    computed<Type<DestinationConfigFormComponent> | null>(
      () => DESTINATION_FORM_REGISTRY[this.registryKey()] ?? null,
    );

  /** Only Csv's and BlobStorage's own components declare `reusingExisting` (gates sftpPassword's/secretValue's
   *  required validator) — see CsvDestinationFormComponent/BlobStorageDestinationFormComponent. Passing an
   *  input key a loaded component doesn't declare would throw (NgComponentOutlet uses ComponentRef.setInput
   *  under the hood), so this is scoped to those branches only. Deliberately a plain method, not computed() —
   *  hasExistingChanged() reads the live FormGroup underneath activeForm(), which isn't itself a tracked
   *  signal, so a computed() here would never invalidate as the user types; template bindings re-evaluate
   *  this fresh on every change-detection pass instead. */
  activeFormInputs(): Record<string, unknown> {
    // Medplum's own form (like Mongo's) has no `reusingExisting` input — passing it would throw via
    // ComponentRef.setInput. FHIR (Aidbox) never reaches this at all (isFhir() is never registry-routed —
    // see registryKey()), so it isn't listed here. AzureFhirServiceDestinationFormComponent DOES declare
    // `reusingExisting` (like Blob's), so 'azurefhir' is deliberately NOT added to this exclusion.
    if (this.isSql() || this.isMongo() || this.isMedplum()) return {};
    return {
      reusingExisting:
        this.connectionMode() === 'existing' && !this.hasExistingChanged(),
    };
  }

  /** Live instance of whatever DESTINATION_FORM_REGISTRY component is currently loaded, or null before the
   *  view has finished initializing. Read fresh on every call (never memoized) — NgComponentOutlet's own
   *  `componentInstance` getter isn't itself a signal, so caching this in a computed() risks freezing on a
   *  stale (pre-creation) null the first time it's read. */
  activeForm(): WizardDestinationFormApi | null {
    return (
      (this.formOutlet()
        ?.componentInstance as WizardDestinationFormApi | null) ?? null
    );
  }

  /** Full dest_*-keyed config bag of whatever's currently loaded — used by the mapping canvas's
   *  connectionInfo/csvDelimiterKey bindings and Step 4's review cards, all of which used to read
   *  sqlForm.value/csvForm.value/mongoForm.value directly. */
  activeFormConfig(): Record<string, string> {
    return this.activeForm()?.getFullConfig() ?? {};
  }

  // A saved node's fields (editing) or a picked existing connection's metadata can arrive before the Step 1
  // form component has actually been created (both can fire during ngOnInit, before the view — and this
  // outlet's child — exists). Queued here and flushed by the effect in the constructor the moment the form
  // instance becomes available, instead of relying on it already being there like the old FormGroup fields.
  private readonly _pendingFormPatch = signal<{
    fields: Record<string, string>;
    target: string | null;
    isExistingSelection?: boolean;
  } | null>(null);

  // Snapshot of Step 1's raw form value taken once, right after the outlet's child first exists — either
  // its blank defaults (brand-new destination) or right after _populateFromNode()'s/selectExisting()'s
  // queued patch has flushed onto it (editing/reusing), same ordering the _pendingFormPatch effect below
  // relies on. Compared against the live value by isStep1Dirty() to tell "closed without ever really
  // touching Step 1" (safe to discard silently) apart from "typed something but never clicked Next/Save"
  // (needs the same confirm-before-discard treatment as switching type or closing the whole dialog already
  // get via _hasProgressed — see cancel()/node-library-dialog's onDestWizardCancelled()).
  private _step1Baseline: Record<string, unknown> | null = null;

  readonly fhirForm = this.fb.group({
    name: ['Aidbox Production', [Validators.required]],
    baseUrl: ['', [Validators.required]],
    project: ['', []],
    authType: ['oauth2', [Validators.required]],
    writeMode: ['upsert', []],
    // ── OAuth2 Client Credentials fields (conditional on authType) ───────────
    tokenEndpoint: ['', []],
    clientId: ['', []],
    clientSecret: ['', []],
    // ── Basic auth fields (conditional) ──────────────────────────────────────
    username: ['', []],
    password: ['', []],
    // ── Bearer token field (conditional) ─────────────────────────────────────
    bearerToken: ['', []],
    // ── Auto-fetch missing references (opt-in) ────────────────────────────────
    // Only ever fetches a reference the write-time existence check already confirmed missing from both this
    // batch and the destination (see MappedFhirRepositoryDestinationWriter.ResolveMissingReferencesAsync) — never
    // a first resort. Requires the source's own app registration to have read access to whatever resource type
    // turns up missing, since it fetches from the same EHR source feeding this destination.
    autoFetchMissingReferences: [false, []],
    autoFetchMaxCount: [25, []],
  });

  // ── FHIR field-handling (step 3: passthrough vs. customize) ──────────────
  // Rules are grouped by resource type (Record keyed by the same resource-type strings selected in step 2's
  // Data groups) rather than one flat list — a rule with no resource type attached was the exact "which
  // resource does this apply to" ambiguity this design replaces.
  readonly fhirMapMode = signal<'passthrough' | 'customize'>('passthrough');
  readonly fhirCustomRules = signal<Record<string, FhirCustomRule[]>>({});
  readonly activeRuleResource = signal<string | null>(null);

  // Real, backend-generated FHIR element catalog (MappingCatalogService — the same source the SQL/Mongo/CSV
  // "Map fields" step already uses, generated from the actual Firely R4 model) rather than a small hand-curated
  // list — so the field picker actually reflects every real element on the resource, not a guessed-at subset.
  readonly fhirFieldCatalog = signal<Record<string, FhirElement[]>>({});
  private readonly _fhirCatalogRequested = new Set<string>();

  private _ensureFhirFieldCatalog(resourceType: string): void {
    if (this._fhirCatalogRequested.has(resourceType)) return;
    this._fhirCatalogRequested.add(resourceType);
    this.catalogSvc
      .fields(resourceType, this.sourceConnectionId(), this.sourceVendor())
      .subscribe((fields) => {
        if (!fields.length) {
          this._fhirCatalogRequested.delete(resourceType);
          return;
        }
        // Drop the mapping engine's virtual @token system fields (@runId, @now, ...) — those only resolve inside
        // MappingNodeExecutor's field-mapping engine, not on the raw resource JSON this screen's rules run against.
        const real = fields.filter((f) => !f.fhirPath.startsWith('@'));
        this.fhirFieldCatalog.update((m) => ({ ...m, [resourceType]: real }));
      });
  }

  selectFhirCustomizeMode(): void {
    this.fhirMapMode.set('customize');
    if (!this.activeRuleResource() && this.selectedResources().length > 0) {
      this.activeRuleResource.set(this.selectedResources()[0]);
    }
    if (this.activeRuleResource()) {
      this._ensureFhirFieldCatalog(this.activeRuleResource()!);
    }
  }

  selectRuleResourceTab(resourceType: string): void {
    this.activeRuleResource.set(resourceType);
    this._ensureFhirFieldCatalog(resourceType);
  }

  rulesForActiveResource(): FhirCustomRule[] {
    const rt = this.activeRuleResource();
    return rt ? (this.fhirCustomRules()[rt] ?? []) : [];
  }

  fieldOptionsFor(
    resourceType: string | null,
  ): { path: string; label: string }[] {
    if (!resourceType) return [];
    return (this.fhirFieldCatalog()[resourceType] ?? []).map((f) => ({
      path: this._withDefaultArrayIndices(f.fhirPath, f.arrays),
      label: f.label || f.fhirPath,
    }));
  }

  // MappingCatalogService's fhirPath is a schema-level path with no array indices (e.g. "identifier.system") —
  // correct for the SQL/Mongo field-mapping engine (which iterates every array element), but this screen's
  // FhirFieldTransformApplier needs a concrete instance path (e.g. "identifier[0].system") to navigate one exact
  // value. Defaults every array-ancestor segment to its first element ("[0]") — the common case (first/primary
  // entry) — "Other field (advanced)" remains the escape hatch for any other index.
  private _withDefaultArrayIndices(fhirPath: string, arrays: string[]): string {
    const arraySet = new Set(arrays);
    let running = '';
    return fhirPath
      .split('.')
      .map((segment) => {
        running = running ? `${running}.${segment}` : segment;
        return arraySet.has(running) ? `${segment}[0]` : segment;
      })
      .join('.');
  }

  fhirTransformLabel(t: FhirTransformType): string {
    return FHIR_TRANSFORM_LABELS[t];
  }

  addFhirRule(): void {
    const rt = this.activeRuleResource();
    if (!rt) return;
    this.fhirCustomRules.update((rules) => ({
      ...rules,
      [rt]: [
        ...(rules[rt] ?? []),
        { field: '', transform: 'dateFormat', params: {} },
      ],
    }));
  }

  removeFhirRule(index: number): void {
    const rt = this.activeRuleResource();
    if (!rt) return;
    this.fhirCustomRules.update((rules) => ({
      ...rules,
      [rt]: (rules[rt] ?? []).filter((_, i) => i !== index),
    }));
  }

  updateFhirRule(index: number, patch: Partial<FhirCustomRule>): void {
    const rt = this.activeRuleResource();
    if (!rt) return;
    this.fhirCustomRules.update((rules) => ({
      ...rules,
      [rt]: (rules[rt] ?? []).map((r, i) =>
        i === index ? { ...r, ...patch } : r,
      ),
    }));
  }

  updateFhirRuleParam(index: number, key: string, value: string): void {
    const rt = this.activeRuleResource();
    if (!rt) return;
    this.fhirCustomRules.update((rules) => ({
      ...rules,
      [rt]: (rules[rt] ?? []).map((r, i) =>
        i === index ? { ...r, params: { ...r.params, [key]: value } } : r,
      ),
    }));
  }

  onFieldSelectChange(index: number, value: string): void {
    if (value === '__custom__') {
      this.updateFhirRule(index, { customField: true, field: '' });
    } else {
      this.updateFhirRule(index, { customField: false, field: value });
    }
  }

  onTransformChange(index: number, value: string): void {
    // Reset params on transform change — a previous transform's params (e.g. fromUnit/toUnit) don't carry
    // any meaning for a newly-chosen transform (e.g. coalesce's defaultValue).
    this.updateFhirRule(index, {
      transform: value as FhirTransformType,
      params: {},
    });
  }

  // ── codeLookup / statusCoercion nested pairs sub-editor ───────────────────
  // params is a flat string map, so the {from,to} pair list is stored as a JSON string under params['pairs'] —
  // parsed/serialized only here, at the UI edge.
  getLookupPairs(rule: FhirCustomRule): FhirLookupPair[] {
    try {
      const parsed = JSON.parse(rule.params['pairs'] || '[]');
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  }

  private setLookupPairs(index: number, pairs: FhirLookupPair[]): void {
    this.updateFhirRuleParam(index, 'pairs', JSON.stringify(pairs));
  }

  addLookupPair(index: number): void {
    this.setLookupPairs(index, [
      ...this.getLookupPairs(this.rulesForActiveResource()[index]),
      { from: '', to: '' },
    ]);
  }

  removeLookupPair(index: number, pairIndex: number): void {
    this.setLookupPairs(
      index,
      this.getLookupPairs(this.rulesForActiveResource()[index]).filter(
        (_, i) => i !== pairIndex,
      ),
    );
  }

  updateLookupPair(
    index: number,
    pairIndex: number,
    key: 'from' | 'to',
    value: string,
  ): void {
    const pairs = this.getLookupPairs(this.rulesForActiveResource()[index]);
    this.setLookupPairs(
      index,
      pairs.map((p, i) => (i === pairIndex ? { ...p, [key]: value } : p)),
    );
  }

  totalFhirRuleCount(): number {
    return Object.values(this.fhirCustomRules()).reduce(
      (sum, rows) => sum + rows.length,
      0,
    );
  }

  fhirFieldLabel(resourceType: string | null, path: string): string {
    if (!path) return '(no field selected)';
    const entry = this.fieldOptionsFor(resourceType).find(
      (f) => f.path === path,
    );
    return entry?.label ?? path;
  }

  /** Plain-language summary shown under each rule — states back in words what the rule does and to what,
   *  rather than leaving the user to infer meaning from disconnected field/transform/param inputs. */
  fhirRulePreview(rule: FhirCustomRule): string {
    const field = this.fhirFieldLabel(this.activeRuleResource(), rule.field);
    switch (rule.transform) {
      case 'dateFormat':
        return `Format ${field} as ${rule.params['targetFormat'] === 'custom' ? rule.params['pattern'] || 'a custom pattern' : 'ISO 8601'}.`;
      case 'typeCast':
        return `Cast ${field} to ${rule.params['targetType'] || 'string'}.`;
      case 'booleanConversion':
        return `Convert ${field} to true/false (true values: ${rule.params['trueValues'] || '—'}).`;
      case 'unitConversion':
        return `Convert ${field} from ${rule.params['fromUnit'] || '?'} to ${rule.params['toUnit'] || '?'}.`;
      case 'quantityRange':
        return rule.params['mode'] === 'range'
          ? `Split ${field} into a low/high Range (unit: ${rule.params['unit'] || '?'}).`
          : `Build a Quantity from ${field} (unit: ${rule.params['unit'] || '?'}).`;
      case 'roundPrecision':
        return `Round ${field} to ${rule.params['decimals'] || '2'} decimal place(s).`;
      case 'codeLookup':
      case 'statusCoercion':
        return `Map ${field} using ${this.getLookupPairs(rule).length} lookup pair(s).`;
      case 'codeableConcept':
        return `Build a CodeableConcept from ${field} (system: ${rule.params['system'] || '?'}).`;
      case 'telecom':
        return `Build a ${rule.params['system'] || 'phone'} ContactPoint from ${field}.`;
      case 'referenceConstruct':
        return `Build a ${rule.params['targetResourceType'] || '?'} reference from ${field}.`;
      case 'identifierFormat':
        return `Build an Identifier from ${field} (system: ${rule.params['system'] || '?'}).`;
      case 'humanNameFormat':
        return `Parse ${field} into a HumanName (split on "${rule.params['delimiter'] || ', '}").`;
      case 'addressParse':
        return `Parse ${field} into an Address (split on "${rule.params['delimiter'] || ','}").`;
      case 'stringClean':
        return `Clean ${field} (${rule.params['mode'] || 'trim'}).`;
      case 'stringTemplate':
        return rule.params['mode'] === 'split'
          ? `Split ${field} on "${rule.params['delimiter'] || ''}".`
          : `Apply template "${rule.params['template'] || '{value}'}" to ${field}.`;
      case 'arrayOp':
        return `Apply ${rule.params['operation'] || 'first'} to ${field}.`;
      case 'coalesce':
        return `Default ${field} to "${rule.params['defaultValue'] || ''}" when missing.`;
      case 'dateShift':
        return `Shift ${field} by ${rule.params['days'] || '0'} day(s).`;
      case 'hashMask':
        return rule.params['mode'] === 'mask'
          ? `Mask ${field}, keeping the last ${rule.params['showLastN'] || '0'} character(s).`
          : `Hash ${field} (SHA-256).`;
      default:
        return `${this.fhirTransformLabel(rule.transform)} on ${field}.`;
    }
  }

  // ── data groups ───────────────────────────────────────────────────────────
  // The full curated FHIR resource list, intersected with whatever the upstream source's live
  // Discover/metadata probe returns (see the sourceConnectionId effect below) once that's known — so a
  // source whose CapabilityStatement only supports e.g. 20 of our 37 catalog resources only ever offers
  // those 20 here, dynamically, without a separately hand-maintained list. Falls back to the full catalog
  // unfiltered whenever discovery hasn't run yet, has no source to run against, or came back inconclusive
  // (no network access to re-probe, endpoint unreachable, etc.) — same as this wizard's behavior before
  // this filter existed, so a destination added before any source is configured still gets the full list.
  readonly discoveredResourceTypes = signal<string[] | null>(null);
  readonly discoverProbeStatus = signal<'idle' | 'probing' | 'done' | 'error'>(
    'idle',
  );
  /** Exposed for the Step 2 hint's "Showing N of {{ SUPPORTED_RESOURCE_TYPES.length }}" — the imported
   *  const itself isn't reachable from the template. */
  readonly SUPPORTED_RESOURCE_TYPES = SUPPORTED_RESOURCE_TYPES;
  readonly availableGroups = computed(() => {
    const discovered = this.discoveredResourceTypes();
    if (!discovered) return SUPPORTED_RESOURCE_TYPES;
    const discoveredSet = new Set(discovered);
    return SUPPORTED_RESOURCE_TYPES.filter((r) => discoveredSet.has(r));
  });
  readonly selectedResources = signal<string[]>([]);
  readonly groupSearchQuery = signal<string>('');
  readonly filteredGroups = computed(() => {
    const q = this.groupSearchQuery().trim().toLowerCase();
    if (!q) return this.availableGroups();
    return this.availableGroups().filter((r) => r.toLowerCase().includes(q));
  });

  onGroupSearchInput(value: string): void {
    this.groupSearchQuery.set(value);
  }

  clearGroupSearch(): void {
    this.groupSearchQuery.set('');
  }

  /** Every resource currently visible (respects an active search filter) that isn't selected yet.
   *  Exposed for the template so the "Select all" action can show/disable itself accurately instead
   *  of always claiming there's something left to add. */
  readonly allFilteredSelected = computed(() => {
    const filtered = this.filteredGroups();
    return filtered.length > 0 && filtered.every((r) => this.isResourceSelected(r));
  });

  /** True when at least one currently-visible (filtered) resource is selected — drives the "Clear"
   *  action's disabled state the same way allFilteredSelected() drives "Select all"'s. */
  readonly anyFilteredSelected = computed(() => {
    const selected = new Set(this.selectedResources());
    return this.filteredGroups().some((r) => selected.has(r));
  });

  /** Selects every currently-visible (filtered) resource in one action — snapshots the target list
   *  first, same reasoning as addAllRecommendedResources(): toggleResource() mutates
   *  selectedResources(), which filteredGroups() doesn't depend on but this method's own loop
   *  shouldn't re-read mid-iteration regardless. Goes through toggleResource() (never a direct
   *  selectedResources.set(...)) so nothing already selected gets double-added and so this stays the
   *  single place resource selection is mutated. Respects an active search — "Select all" while
   *  filtered to "Medication" only selects the Medication* resources actually shown, not the full
   *  SUPPORTED_RESOURCE_TYPES universe. */
  selectAllFilteredResources(): void {
    const toAdd = this.filteredGroups().filter((r) => !this.isResourceSelected(r));
    for (const r of toAdd) {
      this.toggleResource(r);
    }
  }

  /** Deselects every currently-visible (filtered) resource — via toggleResource() so each one still
   *  gets its mappingRows/targetByResource/extraTablesByGroup/payloadFieldsByResource cleanup (see
   *  toggleResource's own comment on why that pruning matters). Scoped to the filtered set, not all
   *  of selectedResources(), so clearing while searched to "Medication" doesn't silently drop an
   *  unrelated resource the user picked earlier and can no longer even see. */
  clearAllFilteredResources(): void {
    const toRemove = this.filteredGroups().filter((r) => this.isResourceSelected(r));
    for (const r of toRemove) {
      this.toggleResource(r);
    }
  }

  /** Backs the single "Select all" checkbox in the Step 2 header — a controlled checkbox (its
   *  [checked]/[indeterminate] come from allFilteredSelected()/anyFilteredSelected(), never the
   *  native DOM state), so this decides the action from that same current signal value rather than
   *  reading $event.target.checked: fully selected -> clear the filtered set; anything else
   *  (partial or empty) -> select the rest of it. */
  toggleSelectAllFiltered(): void {
    if (this.allFilteredSelected()) {
      this.clearAllFilteredResources();
    } else {
      this.selectAllFilteredResources();
    }
  }

  // ── mapping rows ──────────────────────────────────────────────────────────
  readonly mappingRows = signal<MappingRow[]>([]);

  // Per-resource destination target (CSV file name / SQL table), entered once in the group header
  // rather than repeated on every mapping row.
  readonly targetByResource = signal<Record<string, string>>({});

  // ── SQL connection probe (test connection → load tables/columns) ────────────
  readonly sqlTables = signal<DestinationTable[]>([]);
  readonly probeState = signal<'idle' | 'testing' | 'ok' | 'error'>('idle');
  readonly probeError = signal<string | null>(null);

  // ── Mongo connection probe (test connection → load real collection names) ──
  // Copied from MongoFormApi.collections() on a successful "Next" (see next()'s Mongo branch) — Step 1's
  // dynamically-mounted form is gone once Step 2/3 mounts, so the mapping canvas's "+ Add a table" picker
  // (fed this via availableTablesToAddFn below) needs its own copy to keep offering real names for
  // additional resources.
  readonly mongoCollections = signal<string[]>([]);

  // ── extra target tables (child tables added alongside a group's primary table) ──
  // Keyed by data-group name; each entry is a list of additional already-probed SQL
  // table full-names the user chose to also map into for that same group's canvas
  // (e.g. mapping Patient's Contact array into a real dbo.PatientContact table).
  // Existing tables only — no schema authoring (no "create a new table").
  readonly extraTablesByGroup = signal<Record<string, string[]>>({});

  extraTablesFor(group: string): string[] {
    return this.extraTablesByGroup()[group] ?? [];
  }

  /** The canvas computes and emits the new desired list directly (add: appended, remove: filtered) —
   *  this just stores it and cleans up any mappings that targeted a table that's no longer in the list. */
  onExtraTablesChange(tables: string[]): void {
    const g = this.activeMappingGroup();
    if (!g) return;
    const removed = this.extraTablesFor(g).filter((t) => !tables.includes(t));
    this.extraTablesByGroup.update((m) => ({ ...m, [g]: tables }));
    if (removed.length) {
      this.mappingRows.update((rows) =>
        rows.filter(
          (r) => !(r.resource === g && removed.includes(r.tableName)),
        ),
      );
    }
  }

  // ── child-table relations (parent table / parent PK / FK) ───────────────────────────────────────
  // Global, not per-resource/per-canvas — a table's parent/FK relationship doesn't depend on which
  // resource's canvas happens to be open. Previously lived as local state inside
  // FieldMappingCanvasComponent, which meant it was lost the moment a different resource's canvas
  // opened (a fresh component instance) — lifted here so it survives resource navigation, node reload,
  // and the Mapping JSON export/import.
  readonly childTableRelationsByTable = signal<
    Record<string, ChildTableRelation>
  >({});

  onChildTableRelationAdded(e: {
    tableName: string;
    relation: ChildTableRelation;
  }): void {
    this.childTableRelationsByTable.update((m) => ({
      ...m,
      [e.tableName]: e.relation,
    }));
  }

  // ── Mapping JSON — the one canonical save/load contract for this screen (see field-mapping-summary.
  // model.ts). Aggregates every resource with at least one mapping in one shot; independent of
  // dest_mappings/dest_mappings_v2 (still feed workflow-build-assembler.service.ts unmodified). ────────
  readonly lastMappingSummary = signal<MappingSummaryDocument | null>(null);
  readonly mappingSummaryPreviewOpen = signal(false);
  readonly canSaveMappingSummary = computed(
    () => this.mappingRows().length > 0,
  );

  buildAndShowMappingSummary(): void {
    const doc = buildMappingSummaryDocument({
      sourceVendor: this.sourceVendor().toUpperCase(),
      destType: this.nonFhirDestType(),
      destLabel: this.destLabel(),
      mappingRows: this.mappingRows(),
      sqlTables: this.sqlTables(),
      childTableRelationsByTable: this.childTableRelationsByTable(),
      availableFields: this.availableFieldsFn,
      sourceConnectionId: this.sourceConnectionId(),
      destinationId: this.selectedExistingId() ?? this.resolvedDestinationId(),
      targetByResource: this.targetByResource(),
    });
    this.lastMappingSummary.set(doc);
    this.mappingSummarySvc.save(doc).subscribe(() => {
      this.toast.success(
        'Mapping saved',
        `${doc.mappings.length} resource mapping${doc.mappings.length === 1 ? '' : 's'} saved.`,
      );
    });
  }

  closeMappingSummaryPreview(): void {
    this.mappingSummaryPreviewOpen.set(false);
  }

  /** Reconstructs this screen entirely from a previously saved Mapping JSON document — "Edit Mapping". */
  loadMappingSummary(doc: MappingSummaryDocument): void {
    const applied = applyMappingSummaryDocument(doc, this.nonFhirDestType());
    this.mappingRows.set(applied.mappingRows);
    this.targetByResource.set(applied.targetByResource);
    this.extraTablesByGroup.set(applied.extraTablesByGroup);
    this.sqlTables.set(applied.sqlTables);
    if (applied.sqlTables.length) this.probeState.set('ok');
    this.childTableRelationsByTable.set(applied.childTableRelationsByTable);
    this.selectedResources.set(applied.selectedResources);
  }

  // ── "Load mapping JSON" — paste a previously saved Mapping JSON document back in. Stubbed the same
  // way MappingSnapshotService is: no real backend endpoint yet, so this only parses/applies pasted
  // text; MappingSummaryService.save()/load() are ready for a real HttpClient call to slot in later. ──
  readonly mappingSummaryLoadText = signal('');
  readonly mappingSummaryLoadError = signal<string | null>(null);

  submitLoadMappingSummary(): void {
    const raw = this.mappingSummaryLoadText().trim();
    if (!raw) {
      this.mappingSummaryLoadError.set('Paste a Mapping JSON document first.');
      return;
    }
    let doc: MappingSummaryDocument;
    try {
      doc = JSON.parse(raw) as MappingSummaryDocument;
    } catch (e) {
      this.mappingSummaryLoadError.set(`Invalid JSON: ${(e as Error).message}`);
      return;
    }
    if (!Array.isArray(doc?.mappings)) {
      this.mappingSummaryLoadError.set(
        'Not a recognized Mapping JSON document (missing "mappings").',
      );
      return;
    }
    this.mappingSummaryLoadError.set(null);
    this.loadMappingSummary(doc);
    this.toast.success(
      'Mapping loaded',
      `Restored ${doc.mappings.length} resource mapping${doc.mappings.length === 1 ? '' : 's'} from the pasted JSON.`,
    );
  }

  // ── mapping snapshot (independent of the workflow — see mapping-snapshot.model.ts) ─────────────
  // A self-contained save/load for just this screen's state, addressable by its own id, with no
  // dependency on nodes/edges/sources/destinations/trigger/workflowId or a live DB connection to
  // reconstruct. Entirely additive: dest_mappings/dest_mappings_v2-on-the-node (used by the OUTER
  // workflow save, see _save() below) and the existing Save/Close buttons are untouched by this.
  readonly savedSnapshots = signal<MappingSnapshotSummary[]>([]);
  readonly snapshotIdToLoad = signal<string | null>(null);
  readonly snapshotBusy = signal(false);
  /** Set once a snapshot has been loaded this session, so a later Save updates that same record
   *  instead of forking a new one every time. */
  private _loadedSnapshotId: string | null = null;

  refreshSnapshotList(): void {
    this.mappingSnapshotSvc
      .list()
      .subscribe((list) => this.savedSnapshots.set(list));
  }

  private _buildSnapshot(): Omit<
    MappingSnapshot,
    'id' | 'createdAt' | 'updatedAt'
  > {
    const group = this.activeMappingGroup();
    return {
      name: group ? `${group} mapping` : 'Untitled mapping',
      destType: this.nonFhirDestType(),
      selectedResources: this.selectedResources(),
      activeGroup: group,
      targetByResource: this.targetByResource(),
      extraTablesByGroup: this.extraTablesByGroup(),
      destinationTables: this.sqlTables(),
      payloadFieldsByResource: this.payloadFieldsByResource(),
      mappingRows: this.mappingRows(),
      childTableRelationsByTable: this.childTableRelationsByTable(),
    };
  }

  /** Saves ONLY the mapping-screen state — resource type, destination tables/columns (incl. anything
   *  user-created), mapping rows, and which group/resources are selected. No workflow-level data at all. */
  saveMappingSnapshot(): void {
    this.snapshotBusy.set(true);
    const draft = {
      ...this._buildSnapshot(),
      id: this._loadedSnapshotId ?? undefined,
    };
    this.mappingSnapshotSvc.save(draft).subscribe((saved) => {
      this._loadedSnapshotId = saved.id;
      this.snapshotBusy.set(false);
      this.refreshSnapshotList();
      this.toast.success(
        'Mapping snapshot saved',
        `Saved as "${saved.name}" (id: ${saved.id}).`,
      );
    });
  }

  /** Rebuilds the Map Fields screen entirely from a previously saved snapshot — no live DB probe, no
   *  workflow/node context required. */
  loadMappingSnapshot(id: string): void {
    if (!id) return;
    this.snapshotBusy.set(true);
    this.mappingSnapshotSvc.get(id).subscribe((snapshot) => {
      this.snapshotBusy.set(false);
      if (!snapshot) {
        this.toast.error(
          'Load failed',
          'That saved mapping could not be found.',
        );
        return;
      }
      this._restoreFromSnapshot(snapshot);
      this.toast.success('Mapping loaded', `Restored "${snapshot.name}".`);
    });
  }

  private _restoreFromSnapshot(s: MappingSnapshot): void {
    this._loadedSnapshotId = s.id;
    this.selectedResources.set(s.selectedResources);
    this.targetByResource.set(s.targetByResource);
    this.extraTablesByGroup.set(s.extraTablesByGroup);
    this.payloadFieldsByResource.set(s.payloadFieldsByResource);
    this.sqlTables.set(s.destinationTables);
    // So hasSqlTables()/sqlTableOptions() behave as if a real probe just succeeded, without one.
    if (s.destinationTables.length) this.probeState.set('ok');
    this.mappingRows.set(s.mappingRows);
    // ?? {} covers snapshots saved before this field existed.
    this.childTableRelationsByTable.set(s.childTableRelationsByTable ?? {});
    this.selectedGroupForMapping.set(s.activeGroup);
  }

  /** Column names of any already-probed SQL table by its full name — used for extra target tables, and
   *  for the target card's own [columns] rendering (via the canvas's columnsForCardFn). Deliberately
   *  includes identity/computed and foreign-key columns — a table's real shape, PK/FK badges included,
   *  should stay visible; FieldMappingCanvasComponent.isProtectedColumn is what actually stops one of
   *  these from becoming a mapping target, checked at the point a mapping is completed instead of here. */
  readonly columnsForTableFn = (tableFullName: string): string[] => {
    const table = this.sqlTables().find(
      (t) =>
        t.fullName === tableFullName ||
        // MySQL-only bare-name fallback — see FieldMappingCanvasComponent.sqlTableNames' doc comment.
        // SQL Server/PostgreSQL keep strict fullName-only matching.
        (this.isMySql() && t.tableName === tableFullName),
    );
    return table ? table.columns.map((c) => c.name) : [];
  };

  /** Already-known tables/collections not yet used as this group's primary or extra targets — offered in
   *  "+ Add a table"/"+ Add a collection". Sourced from sqlTables() for SQL, mongoCollections() for Mongo —
   *  deliberately NOT gated through hasSqlTables()/sqlTableOptions() (the canvas's own hasSqlTables input,
   *  which also drives isPrimaryTargetValid's "must be a known table" check): a not-yet-created Mongo
   *  collection is a valid primary target (paired with "Create collection if not exists"), so Mongo must
   *  never flip that check on. */
  readonly availableTablesToAddFn = (group: string): string[] => {
    const used = new Set([
      this.targetFor(group),
      ...this.extraTablesFor(group),
    ]);
    const known = this.isMongo()
      ? this.mongoCollections()
      : this.sqlTables().map((t) => t.fullName);
    return known.filter((t) => !used.has(t));
  };

  /** Ad-hoc connection details from Step 1's SQL form — powers the canvas's real ALTER TABLE / CREATE TABLE calls. */
  connectionInfo(): DestinationProbeRequest | null {
    const form = this.activeForm();
    if (!this.isSql() || !isSqlFamilyForm(form)) return null;
    return form.getProbeRequest();
  }

  /** A column was added via the canvas's Add Column modal — upserted (not blindly appended) into the
   *  known schema, since this fires TWICE for the same column: once immediately with a locally-synthesized
   *  preview (see field-mapping-canvas.component.ts's submitAddColumn — no DB call has happened yet at
   *  that point), and again with the backend's authoritative shape once "Add to Pipeline" actually flushes
   *  the queued ALTER TABLE for real (see flushPendingSchemaOps). Tagged 'userCreated' so a MappingSnapshot
   *  can tell it apart from a probed/pre-existing column.
   *  If `table` is set, the target table didn't already exist locally — upserted the same way, by fullName,
   *  rather than trying (and failing) to append to a table that was never there. */
  onColumnAdded(e: {
    tableName: string;
    column: DestinationColumn;
    table?: DestinationTable;
  }): void {
    if (e.table) {
      const tagged = {
        ...e.table,
        origin: 'userCreated' as const,
        columns: e.table.columns.map((c) => ({
          ...c,
          origin: 'userCreated' as const,
        })),
      };
      this.sqlTables.update((tables) => {
        const idx = tables.findIndex((t) => t.fullName === tagged.fullName);
        return idx === -1
          ? [...tables, tagged]
          : tables.map((t, i) => (i === idx ? tagged : t));
      });
      return;
    }
    const column: DestinationColumn = { ...e.column, origin: 'userCreated' };
    this.sqlTables.update((tables) =>
      tables.map((t) => {
        if (t.fullName !== e.tableName) return t;
        const idx = t.columns.findIndex((c) => c.name === column.name);
        const columns =
          idx === -1
            ? [...t.columns, column]
            : t.columns.map((c, i) => (i === idx ? column : c));
        return { ...t, columns };
      }),
    );
  }

  /** A table was created via the canvas's "Create a new table…" modal — upserted (not skipped when
   *  already present) into the known schema, since this fires TWICE for the same table: once immediately
   *  with a locally-synthesized preview (no DB call has happened yet at that point — see
   *  field-mapping-canvas.component.ts's submitCreateTable), and again with the backend's authoritative
   *  shape (including its FK column, if this was made a child table) once "Add to Pipeline" actually
   *  flushes the queued CREATE TABLE for real (see flushPendingSchemaOps). Tagged 'userCreated' (table and
   *  all its columns) for the same reason as onColumnAdded. */
  onTableCreated(table: DestinationTable): void {
    const tagged = {
      ...table,
      origin: 'userCreated' as const,
      columns: table.columns.map((c) => ({
        ...c,
        origin: 'userCreated' as const,
      })),
    };
    this.sqlTables.update((tables) => {
      const idx = tables.findIndex((t) => t.fullName === tagged.fullName);
      return idx === -1
        ? [...tables, tagged]
        : tables.map((t, i) => (i === idx ? tagged : t));
    });
  }

  /** A column was really dropped via the canvas's delete-column flow — remove it from the known schema. */
  onColumnDropped(e: { tableName: string; column: string }): void {
    this.sqlTables.update((tables) =>
      tables.map((t) =>
        t.fullName === e.tableName
          ? { ...t, columns: t.columns.filter((c) => c.name !== e.column) }
          : t,
      ),
    );
  }

  /** A column was really altered (rename and/or data type change) — update its entry in sqlTables()
   *  (preserving its 'userCreated'/'probed' origin) and, if it was renamed, re-point any mapping that
   *  targeted the old column name so it doesn't silently point at a column that no longer exists. */
  onColumnAltered(e: {
    tableName: string;
    oldColumnName: string;
    column: DestinationColumn;
  }): void {
    this.sqlTables.update((tables) =>
      tables.map((t) =>
        t.fullName === e.tableName
          ? {
              ...t,
              columns: t.columns.map((c) =>
                c.name === e.oldColumnName
                  ? { ...e.column, origin: c.origin }
                  : c,
              ),
            }
          : t,
      ),
    );
    if (e.column.name !== e.oldColumnName) {
      this.mappingRows.update((rows) =>
        rows.map((r) =>
          r.tableName === e.tableName && r.targetName === e.oldColumnName
            ? { ...r, targetName: e.column.name }
            : r,
        ),
      );
    }
  }

  /** A real FHIR JSON payload was pasted and parsed via the canvas's "Load JSON payload" modal —
   *  store its fields so the source tree for this resource mirrors the payload's actual shape. */
  onSourcePayloadLoaded(e: {
    resource: string;
    fields: ResourceFieldDef[];
  }): void {
    this.payloadFieldsByResource.update((m) => ({
      ...m,
      [e.resource]: e.fields,
    }));
  }

  // ── deferred schema DDL (create table / add / drop / alter column) ─────────────────────────
  // Every schema-authoring action on the canvas is staged here instead of hitting the database the
  // moment the user clicks it — nothing real happens until "Add to Pipeline" flushes this queue (see
  // next()/onSave() calling flushPendingSchemaOps below). The canvas has already applied a local preview
  // of each op by the time it's queued (see field-mapping-canvas.component.ts), so the mapping UI works
  // normally throughout; this queue exists purely to run the real DDL, in order, once confirmed.
  readonly pendingSchemaOps = signal<PendingSchemaOp[]>([]);
  readonly applyingSchemaOps = signal(false);
  /** Every table name any currently-queued op still references — passed down to the canvas so its own
   *  "Create a new table…"/"Add column" guards can tell "already exists for real" apart from "already
   *  staged this session, not yet flushed" (see field-mapping-schema-ops.util.ts — this is the fix for
   *  the canvas contradicting a live-database check with a local-only "already on this canvas" one). */
  readonly pendingTableNames = computed(() => computePendingTableNames(this.pendingSchemaOps()));

  onSchemaOpQueued(op: PendingSchemaOp): void {
    this.pendingSchemaOps.update((ops) => [...ops, op]);
  }

  /** A table was removed from the canvas (FieldMappingCanvasComponent.confirmRemoveTable) — drop any of
   *  ITS OWN still-queued schema ops along with it (the "createTable" op that made it exist as a preview
   *  in the first place, or an "addColumn"/"alterColumn"/"dropColumn" queued on it before removal).
   *  Without this, a table the user created and then removed left its queue entries dangling: nothing in
   *  the canvas still shows or maps to the table, but "Add to Workflow" would still create it for real,
   *  and validateMappingForSave's own pendingSchemaOps check would keep naming a table no longer on
   *  screen. Filtering by name is harmless even when nothing matches (a real, already-probed table
   *  removed via its extra-table "✕" was never queued in the first place). */
  onSchemaOpsCancelledForTable(tableName: string): void {
    this.pendingSchemaOps.update((ops) =>
      ops.filter((op) => op.request.tableName !== tableName),
    );
  }

  /** Runs every queued schema op for real, strictly in the order they were queued (a later op — e.g. add
   *  column — may depend on an earlier one — e.g. create table — having actually landed first). Stops at
   *  the first failure: already-applied ops are dropped from the queue (so a retry doesn't repeat them),
   *  the failing op and anything still after it stay queued, and the caller is told not to proceed with
   *  the rest of the save so a mapping profile is never persisted against schema that doesn't exist. */
  flushPendingSchemaOps(): Observable<boolean> {
    if (this.pendingSchemaOps().length === 0) return of(true);

    this.applyingSchemaOps.set(true);
    // The actual "run in order, stop at the first failure" sequencing is a pure, DI-free algorithm — see
    // field-mapping-schema-ops.util.ts's runQueuedOpsSequentially — so it's unit-testable without a live
    // database or Angular's TestBed. Reads pendingSchemaOps() fresh on each call rather than closing over
    // a snapshot, matching the previous behavior exactly.
    return runQueuedOpsSequentially(
      this.pendingSchemaOps(),
      (op) => this._applyOneSchemaOp(op),
      (op) => this.pendingSchemaOps.update((list) => list.filter((o) => o !== op)),
    ).pipe(
      catchError((err) => {
        const msg =
          err instanceof Error
            ? err.message
            : 'Failed to apply a queued schema change.';
        this.toast.error('Schema change failed', msg);
        return of(false);
      }),
      finalize(() => this.applyingSchemaOps.set(false)),
    );
  }

  private _applyOneSchemaOp(op: PendingSchemaOp): Observable<void> {
    switch (op.kind) {
      case 'createTable':
        return this.schemaSvc.createTable(op.request).pipe(
          map((result) => {
            if (!result.success)
              throw new Error(
                `Create table "${op.request.tableName}" failed: ${result.error ?? 'unknown error'}`,
              );
            if (result.table) this.onTableCreated(result.table);
          }),
        );
      case 'addColumn':
        return this.schemaSvc.addColumn(op.request).pipe(
          map((result) => {
            if (!result.success || !result.column)
              throw new Error(
                `Add column "${op.request.columnName}" on ${op.request.tableName} failed: ${result.error ?? 'unknown error'}`,
              );
            this.onColumnAdded({
              tableName: op.request.tableName,
              column: result.column,
              table: result.table ?? undefined,
            });
          }),
        );
      case 'dropColumn':
        return this.schemaSvc.dropColumn(op.request).pipe(
          map((result) => {
            if (!result.success)
              throw new Error(
                `Drop column "${op.request.columnName}" on ${op.request.tableName} failed: ${result.error ?? 'unknown error'}`,
              );
          }),
        );
      case 'alterColumn':
        return this.schemaSvc.alterColumn(op.request).pipe(
          map((result) => {
            if (!result.success || !result.column)
              throw new Error(
                `Update column "${op.request.columnName}" on ${op.request.tableName} failed: ${result.error ?? 'unknown error'}`,
              );
            this.onColumnAltered({
              tableName: op.request.tableName,
              oldColumnName: op.request.columnName,
              column: result.column,
            });
          }),
        );
    }
  }

  // ── select an existing DestinationConfiguration instead of building a new one ───────────────
  // Only offered when attaching a brand-new destination node (not when editing one already on the canvas —
  // that node's fields already pin a connection, existing or otherwise). Excludes destinations that already
  // have pipeline execution history: the workflow-build endpoint re-submits this step's form as an update
  // against the chosen id, which the backend now rejects (409) once a destination has run history, since an
  // update there would silently overwrite a record other routes depend on staying put.
  readonly connectionMode = signal<'new' | 'existing'>('new');
  readonly showConnectionModeToggle = computed(() => !this.editNode());
  readonly existingOptions = signal<DestinationConfigurationDto[]>([]);
  readonly existingOptionsLoading = signal(false);
  readonly selectedExistingId = signal<string | null>(null);

  // Cross-cutting concern independent of destination type, so it lives at the wizard level rather than in
  // any one DESTINATION_FORM_REGISTRY form component — see provisionDestinationConnection()'s four request
  // branches and Step 4's review summary.
  readonly deIdentificationProfiles = signal<DeIdentificationProfileDto[]>([]);
  readonly selectedDeIdentificationProfileId = signal<string | null>(null);
  readonly newProfileName = signal('');
  readonly creatingProfile = signal(false);
  readonly selectedDeIdentificationProfileName = computed(() => {
    const id = this.selectedDeIdentificationProfileId();
    return id ? (this.deIdentificationProfiles().find(p => p.id === id)?.name ?? 'None') : 'None';
  });

  private loadDeIdentificationProfiles(): void {
    this.deIdentificationProfileSvc.list().subscribe({
      next: profiles => this.deIdentificationProfiles.set(profiles),
      error: () => this.deIdentificationProfiles.set([]),
    });
  }

  createDeIdentificationProfile(): void {
    const name = this.newProfileName().trim();
    if (!name) {
      return;
    }

    this.creatingProfile.set(true);
    this.deIdentificationProfileSvc.create({ name }).subscribe({
      next: profile => {
        this.deIdentificationProfiles.update(existing => [...existing, profile]);
        this.selectedDeIdentificationProfileId.set(profile.id);
        this.newProfileName.set('');
        this.creatingProfile.set(false);
      },
      error: err => {
        this.creatingProfile.set(false);
        const msg = err?.error?.title ?? err?.error?.error ?? err?.message ?? 'Failed to create the profile.';
        this.toast.show('Profile not created', typeof msg === 'string' ? msg : 'Failed to create the profile.');
      },
    });
  }

  // Set from the edited node's own fields when a prior build already provisioned a real
  // DestinationConfiguration for it (see workflow-builder.component.ts's stampBuildResultIds) — distinct
  // from selectedExistingId, which only reflects a manual "use an existing connection" pick in Step 1.
  // Also set the moment Step 1 (Configure) is completed in "new connection" mode — see
  // provisionDestinationConnection() — so a real destinationId exists as soon as the connection is
  // configured, not only after the whole wizard finishes and the workflow gets built.
  readonly resolvedDestinationId = signal<string | null>(null);
  // The real secret reference for resolvedDestinationId — stamped alongside it by provisionDestinationConnection()
  // (new-connection path) or restored from the node's own config by _populateFromNode() (editing a prior save).
  // _save() needs these on the "new connection" branch so the node carries a real secretKeyVaultName/secretName
  // instead of leaving them blank (ConfigurationSecretProvider throws "Secret '' was not found for vault ''" at
  // run time otherwise).
  readonly resolvedSecretKeyVaultName = signal<string | null>(null);
  readonly resolvedSecretName = signal<string | null>(null);
  readonly provisioningDestination = signal(false);

  private static readonly SQL_TYPES: DestinationType[] = [
    'SqlServer',
    'AzureSql',
    'PostgreSql',
    'MySql',
  ];
  private static readonly CSV_TYPES: DestinationType[] = ['Csv', 'Sftp'];
  private static readonly MONGO_TYPES: DestinationType[] = ['Mongo'];
  private static readonly MEDPLUM_TYPES: DestinationType[] = ['Medplum'];
  private static readonly FHIR_TYPES: DestinationType[] = ['FhirRepository'];
  private static readonly BLOB_TYPES: DestinationType[] = ['BlobStorage'];
  private static readonly AZUREFHIR_TYPES: DestinationType[] = ['AzureFhirService'];

  // ── computed helpers ──────────────────────────────────────────────────────
  // MySQL/PostgreSQL reuse the SQL family's form/steps (server/database/auth + live table/column introspection) —
  // only the probed destinationType and saved transformId differ from SQL Server. Mongo/Blob are their own
  // families: no live introspection, so each gets its own form/branches rather than reusing SQL's or CSV's.
  readonly isSql = computed(
    () =>
      this.destType() === 'sql' ||
      this.destType() === 'mysql' ||
      this.destType() === 'postgres',
  );
  readonly isMySql = computed(() => this.destType() === 'mysql');
  readonly isPostgres = computed(() => this.destType() === 'postgres');
  readonly isMongo = computed(() => this.destType() === 'mongo');
  // Medplum is a FHIR R4 server destination: columnless (writes whole resources), no live schema probe,
  // a single target (the FHIR base URL) and an opaque secret. Its own form/branches, like Mongo.
  readonly isMedplum = computed(() => this.destType() === 'medplum');
  /** A FHIR-native repository (Aidbox) — writes whole FHIR resources, so it has no field-mapping canvas of
   *  its own; step 3 offers passthrough vs. per-field transform rules instead. */
  readonly isFhir = computed(() => this.destType() === 'fhir');
  /** Azure FHIR Service (Azure Health Data Services) — likewise FHIR-native/columnless, no field-mapping
   *  canvas of its own; behaves like isFhir()/isMedplum() everywhere Step 3 gates the mapping canvas, but
   *  (unlike them) is registry-routed — see registryKey() — since its own form declares `reusingExisting`. */
  readonly isAzureFhir = computed(() => this.destType() === 'azurefhir');
  readonly isBlob = computed(() => this.destType() === 'blob');
  /** MySQL/PostgreSQL only — SQL Server always negotiates encryption regardless, so no SSL toggle for it. */
  readonly showSslToggle = computed(() => this.isMySql() || this.isPostgres());

  /** Narrowing helper for everything that genuinely needs a mappable destination (the field-mapping canvas,
   *  buildMappingSummaryDocument, MappingSnapshot, _rebuildRows). Those are all reached only when isFhir() and
   *  isAzureFhir() are both false — neither FHIR-native branch renders the canvas or builds a mapping document —
   *  but signal call sites can't be narrowed by control flow (destType() and isFhir()/isAzureFhir() are
   *  independent function calls to the type checker), so the cast is made explicit and centralized here.
   *  Every caller must guard on `isFhir() || isAzureFhir()` first, exactly as the constructor's row-rebuild
   *  effect does. */
  nonFhirDestType(): MappingDestType {
    return this.destType() as MappingDestType;
  }

  readonly destLabel = computed(() =>
    this.destType() === 'sql'
      ? 'SQL Server'
      : this.destType() === 'mysql'
        ? 'MySQL'
        : this.destType() === 'postgres'
          ? 'PostgreSQL'
          : this.destType() === 'mongo'
            ? 'MongoDB'
            : this.destType() === 'medplum'
              ? 'Medplum'
              : this.destType() === 'fhir'
                ? 'Aidbox'
                : this.destType() === 'azurefhir'
                  ? 'Azure FHIR Service'
                  : this.destType() === 'blob'
                    ? 'Azure Blob Storage'
                    : 'CSV',
  );
  readonly resourceKeys = computed(() => this.selectedResources());

  /** Resource field/target definition — the built-in catalog entry, or a generic fallback for any other resource. */
  private defFor(r: string): ResourceDef {
    return DEST_RESOURCE_DEFS[r] ?? genericResourceDef(r);
  }

  constructor() {
    // Rebuild mapping rows whenever selected resources or destType change
    effect(() => {
      const resources = this.selectedResources();
      const type = this.nonFhirDestType();
      // A FHIR repository (or Azure FHIR Service) has no per-resource table/file target and no mapping rows
      // at all — seeding either would only leave dead state behind on a destination that never renders the
      // mapping canvas.
      if (this.isFhir() || this.isAzureFhir()) return;
      untracked(() => this._rebuildRows(resources, type));
    });

    // Flushes a queued patchFrom() (see _populateFromNode()/selectExisting()) onto the Step 1 form component
    // the moment it actually exists — both editing a saved node and picking an existing connection can fire
    // before the outlet's child is created (ngOnInit runs before the view/its children do), unlike the old
    // FormGroup fields, which existed synchronously as class fields from the constructor onward.
    effect(() => {
      const outlet = this.formOutlet();
      const pending = this._pendingFormPatch();
      if (!outlet || !pending) return;
      // Deferred via afterNextRender() even on this first attempt, not just the retries inside
      // _flushPendingFormPatch — applying the patch synchronously here (mid-render, since this effect fires
      // as part of the newly-created component's own initial change-detection pass) flips the Step 1 form
      // from invalid to valid *during* that same pass, which trips NG0100
      // (ExpressionChangedAfterItHasBeenCheckedError) on isNextDisabled()'s "Test connection & Next" binding.
      // Running after the render is done avoids fighting Angular's own dev-mode consistency check.
      untracked(() =>
        afterNextRender(() => this._flushPendingFormPatch(pending), {
          injector: this.injector,
        }),
      );
    });

    // Captures Step 1's pristine baseline exactly once per wizard instance — runs right after the effect
    // above on the same flush (registration order), so a queued edit/existing-connection patch has already
    // landed on the form by the time this reads it. Same retry rationale as that effect (see
    // _flushPendingFormPatch's doc comment) — getRawValue() can hit the exact same not-ready-yet outlet.
    effect(() => {
      const outlet = this.formOutlet();
      if (!outlet || this._step1Baseline !== null) return;
      untracked(() =>
        afterNextRender(() => this._captureStep1Baseline(), {
          injector: this.injector,
        }),
      );
    });
    // FHIR bypasses the outlet entirely (see isFhir()'s doc comment), so the effect above never fires for
    // it — fhirForm exists synchronously as a class field, so its baseline can be captured immediately
    // rather than waiting on afterNextRender()/an outlet that will never mount.
    effect(() => {
      if (!this.isFhir() || this._step1Baseline !== null) return;
      untracked(() => {
        this._step1Baseline = this.fhirForm.getRawValue();
      });
    });

    // FHIR auth fields required only while their auth type is selected.
    this._syncFhirAuthValidators(this.fhirForm.controls.authType.value);
    this.fhirForm.controls.authType.valueChanges.subscribe((v) =>
      this._syncFhirAuthValidators(v),
    );

    effect(() => this.stepChange.emit(this.step()));
    effect(() => this.progressChange.emit(this._hasProgressed()));
    effect(() =>
      this.mappingCanvasActive.emit(this.activeMappingGroup() !== null),
    );
    effect(() => {
      const g = this.activeMappingGroup();
      this.mappingCanvasTitle.emit(g ? `Map fields — ${g}` : null);
    });
    // Scoped to the currently open group's own rows — mappingRows() itself holds every resource's mapping for
    // this whole destination, not just the one open in the canvas (see saveGroupMapping/validateMappingForSave,
    // which filter the same way for the same reason). Unfiltered, this badge showed the workflow-wide total
    // (e.g. "437 field mappings") next to a "Map fields — Patient" title that only ever meant Patient's own.
    effect(() =>
      this.mappingCountChange.emit(
        this.mappingRows().filter(
          (r) => r.resource === this.activeMappingGroup(),
        ).length,
      ),
    );

    // Header-level Close/Save trigger counters — react only on an actual increment while this wizard
    // instance is alive. The first run just learns the real baseline (whatever the ancestor's counter
    // already sits at) rather than acting on it — that first value is never "the user just clicked",
    // it's this instance catching up.
    effect(() => {
      const v = this.exitMappingRequest();
      if (this._lastExitTrigger === null) {
        this._lastExitTrigger = v;
        return;
      }
      if (v !== this._lastExitTrigger) {
        this._lastExitTrigger = v;
        if (v > 0) this.requestExitMapping();
      }
    });
    effect(() => {
      const v = this.saveMappingRequest();
      if (this._lastSaveTrigger === null) {
        this._lastSaveTrigger = v;
        return;
      }
      if (v !== this._lastSaveTrigger) {
        this._lastSaveTrigger = v;
        if (v > 0) this.saveGroupMapping();
      }
    });
    effect(() => {
      const v = this.saveSnapshotRequest();
      if (this._lastSaveSnapshotTrigger === null) {
        this._lastSaveSnapshotTrigger = v;
        return;
      }
      if (v !== this._lastSaveSnapshotTrigger) {
        this._lastSaveSnapshotTrigger = v;
        if (v > 0) this.saveMappingSnapshot();
      }
    });
    effect(() => {
      const v = this.markAsMasterRequest();
      if (this._lastMarkAsMasterTrigger === null) {
        this._lastMarkAsMasterTrigger = v;
        return;
      }
      if (v !== this._lastMarkAsMasterTrigger) {
        this._lastMarkAsMasterTrigger = v;
        if (v > 0) this.markActiveGroupAsMaster();
      }
    });

    // Fetch the array-aware FHIR catalog for every data group on offer. The field picker prefers it
    // over the built-in fallback once loaded. Deduped via _requested, keyed on sourceConnectionId too —
    // so if that only becomes known partway through this session (e.g. after a save/build round-trip
    // stamps a real id onto the source node), this refetches with it instead of staying stuck on
    // whatever (generic-catalog) result was fetched back when it was still null. sourceVendor is passed
    // alongside it as a fallback for a source node that hasn't been saved yet at all (so has no real
    // connection id) but already has a vendor picked in its own form (e.g. the Epic wizard's EHR
    // selector defaults to "Epic" the moment it's dropped on the canvas) — without this, a brand-new
    // Epic source would show the generic catalog until the first save round-trip.
    effect(() => {
      const sourceConnectionId = this.sourceConnectionId();
      const sourceVendor = this.sourceVendor();
      for (const r of this.availableGroups())
        this._ensureCatalog(r, sourceConnectionId, sourceVendor);
    });

    // Populates discoveredResourceTypes (see availableGroups above) from whichever source is available,
    // in priority order:
    //  1. sourceDiscoveredResourceTypes input — this canvas session's own Discover result, already fetched
    //     by EhrVendorSourceFormComponent, no extra network call needed. Available the moment the source
    //     form is saved, even before a real sourceConnectionId exists.
    //  2. A live re-probe via sourceConnectionId — fallback for editing a destination on an already-saved
    //     workflow whose source form hasn't been reopened this session, so carries no node-level
    //     'Discovered resource types' field yet.
    // A null/empty result from both resolves to discoveredResourceTypes=null, which availableGroups treats
    // as "show everything" — this filter can only ever narrow the list, never leave the user with nothing
    // to pick from.
    effect(() => {
      const fromNode = this.sourceDiscoveredResourceTypes();
      const id = this.sourceConnectionId();

      if (fromNode.length > 0) {
        this._probedConnectionId = id; // mark handled so a later id-only change doesn't also fire a live probe
        this.discoveredResourceTypes.set(fromNode);
        this.discoverProbeStatus.set('done');
        return;
      }

      if (id === this._probedConnectionId) return;
      this._probedConnectionId = id;
      if (!id) {
        this.discoveredResourceTypes.set(null);
        this.discoverProbeStatus.set('idle');
        return;
      }
      this.discoverProbeStatus.set('probing');
      this.sourceConnectionSvc
        .getById(id)
        .pipe(
          switchMap((conn) => this.discoverySvc.discover(conn.baseUrl)),
          catchError(() => of(null)),
        )
        .subscribe((result) => {
          // The session's own Discover result (fromNode, above) may have arrived while this slower live
          // probe was in flight — that's the more authoritative, already-in-session source, so don't let a
          // late network response clobber it.
          if (this.sourceDiscoveredResourceTypes().length > 0) return;
          if (result && result.resourceTypes.length > 0) {
            this.discoveredResourceTypes.set(result.resourceTypes);
            this.discoverProbeStatus.set('done');
          } else {
            this.discoveredResourceTypes.set(null);
            this.discoverProbeStatus.set('error');
          }
        });
    });
  }

  /** Flushes a queued patchFrom() (see _populateFromNode()/selectExisting()) onto the Step 1 form component.
   *  Re-reads formOutlet() fresh on every attempt rather than closing over a single snapshot, because the
   *  failure mode here isn't just "the SQL-family wrapper's nested viewChild throws" (see the try/catch
   *  below) — NgComponentOutlet's own directive instance can already exist (formOutlet() truthy) *before*
   *  it has actually instantiated its dynamic child, so `outlet.componentInstance` alone can be `null` with
   *  nothing thrown at all. That's a silent, permanent drop: neither `formOutlet()` nor `_pendingFormPatch()`
   *  change again afterward, so the effect that got us here never re-fires on its own. Retrying via
   *  afterNextRender() on *both* the "still null" and "threw" cases is what actually closes the gap. */
  private _flushPendingFormPatch(pending: {
    fields: Record<string, string>;
    target: string | null;
    isExistingSelection?: boolean;
  }): void {
    const form = this.formOutlet()
      ?.componentInstance as WizardDestinationFormApi | null;
    if (!form) {
      afterNextRender(() => this._flushPendingFormPatch(pending), {
        injector: this.injector,
      });
      return;
    }
    try {
      form.patchFrom(pending.fields, pending.target);
      if (pending.isExistingSelection)
        this._existingBaseline = form.getRawValue();
      this._pendingFormPatch.set(null);
    } catch (err) {
      console.error(
        'Step 1 form was not ready to restore its saved values — retrying after next render.',
        err,
      );
      afterNextRender(() => this._flushPendingFormPatch(pending), {
        injector: this.injector,
      });
    }
  }

  /** Captures Step 1's pristine baseline the moment the form actually exists — see isStep1Dirty(). Same
   *  "outlet exists but componentInstance is still null" race as _flushPendingFormPatch above, so it
   *  retries the same way rather than reading formOutlet() once and giving up. */
  private _captureStep1Baseline(): void {
    const form = this.formOutlet()
      ?.componentInstance as WizardDestinationFormApi | null;
    if (!form) {
      afterNextRender(() => this._captureStep1Baseline(), {
        injector: this.injector,
      });
      return;
    }
    try {
      this._step1Baseline = form.getRawValue();
    } catch {
      afterNextRender(() => this._captureStep1Baseline(), {
        injector: this.injector,
      });
    }
  }

  private readonly _requested = new Set<string>();
  // undefined = the probe effect hasn't run yet; null/string thereafter = the last sourceConnectionId it
  // actually probed for, so a redundant re-fire with the same id (e.g. an unrelated signal read in the same
  // effect) doesn't re-probe.
  private _probedConnectionId: string | null | undefined = undefined;

  private _ensureCatalog(
    resource: string,
    sourceConnectionId: string | null,
    sourceVendor: string,
  ): void {
    const key = `${resource}::${sourceConnectionId ?? ''}::${sourceVendor}`;
    if (this._requested.has(key)) return;
    this._requested.add(key);
    this.catalogSvc
      .fields(resource, sourceConnectionId, sourceVendor)
      .subscribe((fields) => {
        if (!fields.length) {
          this._requested.delete(key);
          return;
        }
        const defs = fields.map((f) => this._toFieldDef(resource, f));
        this.catalogByResource.update((m) => ({ ...m, [resource]: defs }));
      });
  }

  private _toFieldDef(resource: string, f: FhirElement): ResourceFieldDef {
    const column = f.fhirPath
      .split('.')
      .map((s) => s.charAt(0).toUpperCase() + s.slice(1))
      .join('');
    return {
      label: f.label || f.fhirPath,
      path: `${resource}.${f.fhirPath}`,
      sqlColumn: column,
      csvColumn: column,
      jsonPath: f.jsonPath,
      valueType: f.valueType,
      arrays: f.arrays,
      referenceTargetTypes: f.referenceTargetTypes,
    };
  }

  // Reverse of buildConnectionMetadata's oauth2 -> clientCredentials bridge, for populating the wizard from an
  // existing DestinationConfiguration's real dest_fhirAuthType value. 'none' has no representation in this
  // wizard's dropdown (oauth2/basic/bearer only), so it — like an absent value — falls back to the default.
  private _fhirAuthTypeFromBackend(
    value: string | undefined,
  ): 'oauth2' | 'basic' | 'bearer' {
    return value === 'basic' || value === 'bearer' ? value : 'oauth2';
  }

  // Reverse of _buildConnectionConfig()'s writeMode split: dest_fhirWriteMode: 'bundle'/'transaction' always means
  // the dropdown's matching 'upsertBundle'/'upsertTransaction' option, regardless of whatever dest_writeMode says —
  // a destination saved before bundling existed simply won't have dest_fhirWriteMode at all, and falls through to
  // dest_writeMode as before. 'conditional' is a stale value from the now-removed "Conditional update by
  // identifier" option (never implemented server-side — it behaved identically to plain upsert) — remap it to
  // 'upsert' so an old destination saved with it still shows a valid, matching dropdown selection.
  private _fhirWriteModeFromBackend(
    destWriteMode: string | undefined,
    destFhirWriteMode: string | undefined,
  ): string {
    if (destFhirWriteMode === 'bundle') return 'upsertBundle';
    if (destFhirWriteMode === 'transaction') return 'upsertTransaction';
    if (destWriteMode === 'conditional') return 'upsert';
    return destWriteMode || 'upsert';
  }

  private _syncFhirAuthValidators(authType: string | null): void {
    // tokenEndpoint is deliberately NOT in this list — it's no longer user-entered. For oauth2/clientCredentials
    // it's discovered from baseUrl (GET {baseUrl}/.well-known/smart-configuration) during testFhirConnection()
    // and patched into this same control, so it still ends up in dest_tokenEndpoint at save time.
    (['clientId', 'clientSecret'] as const).forEach((name) => {
      const ctrl = this.fhirForm.get(name)!;
      ctrl.setValidators(authType === 'oauth2' ? [Validators.required] : []);
      ctrl.updateValueAndValidity({ emitEvent: false });
    });
    (['username', 'password'] as const).forEach((name) => {
      const ctrl = this.fhirForm.get(name)!;
      ctrl.setValidators(authType === 'basic' ? [Validators.required] : []);
      ctrl.updateValueAndValidity({ emitEvent: false });
    });
    const bearerToken = this.fhirForm.get('bearerToken')!;
    bearerToken.setValidators(
      authType === 'bearer' ? [Validators.required] : [],
    );
    bearerToken.updateValueAndValidity({ emitEvent: false });
  }

  ngOnInit(): void {
    this.transformationRulesSvc
      .isHidden()
      .subscribe((hidden) => this.rulesHidden.set(hidden));
    this.refreshSnapshotList();
    this.loadDeIdentificationProfiles();
    const edit = this.editNode();
    if (edit) {
      this._populateFromNode(edit);
      return;
    }
    // New destination: no data group is pre-selected — the user picks explicitly, even when an upstream
    // source is connected and could otherwise offer a default.
    // No explicit New/Existing toggle — the "Existing connection" dropdown is just always there, so load its
    // options unconditionally instead of waiting for a "switch to Existing" step that no longer exists.
    if (this.showConnectionModeToggle()) {
      this._loadExistingOptions();
    }
  }

  // ── step helpers ──────────────────────────────────────────────────────────
  isStepActive(n: number) {
    return this.step() === n;
  }
  isStepDone(n: number) {
    return this.step() > n;
  }

  isNextDisabled(): boolean {
    const s = this.step();
    if (s === 1) {
      if (this.connectionMode() === 'existing' && !this.selectedExistingId())
        return true;
      // FHIR additionally requires a successful Test Connection before advancing — unlike SQL/Mongo/CSV/Blob,
      // wrong credentials here don't surface as a form-validation error (the fields themselves are well-formed
      // strings), so form validity alone would let someone click through with credentials already known to be
      // wrong. Mirrors the FHIR branch in next(), which likewise gates on probeState().
      if (this.isFhir()) return this.fhirForm.invalid;
      const form = this.activeForm();
      return !form || !form.isValid();
    }
    if (s === 2) return this.selectedResources().length === 0;
    return false;
  }

  // ── navigation ────────────────────────────────────────────────────────────
  next(): void {
    if (this.step() === 1) {
      // FHIR (Aidbox) is hand-rolled, not registry-routed (see isFhir()'s doc comment) — leaving Configure
      // first tests the base URL and credentials against the real server, and only provisions/advances once
      // that succeeds. There's no schema to introspect, so the test just validates the connection (and
      // discovers the OAuth2 token endpoint).
      if (this.isFhir()) {
        if (this.probeState() !== 'ok') {
          this.testFhirConnection();
          return;
        }
        const metadata = this._getFhirMetadata();
        if (metadata)
          this.provisionDestinationConnection(metadata, () =>
            this._advancePastStep1(),
          );
        return;
      }

      const form = this.activeForm();
      if (!form) return;
      // SQL: leaving Configure auto-tests the connection and loads tables before advancing; provisioning the
      // real DestinationConfiguration happens once the probe succeeds, right before advancing.
      if (isSqlFamilyForm(form) && form.probeState() !== 'ok') {
        form.testConnection((result) => {
          if (!result.connected) return;
          this.sqlTables.set(result.tables);
          this.probeState.set('ok');
          const metadata = form.getMetadata();
          if (metadata)
            this.provisionDestinationConnection(metadata, () =>
              this._advancePastStep1(),
            );
        });
        return;
      }
      // Mongo: same "test then advance" gate as SQL, so a collection that doesn't exist (and isn't opted into
      // auto-create via the form's checkbox) blocks Next here instead of only failing at pipeline-run time.
      if (isMongoForm(form) && form.probeState() !== 'ok') {
        form.testConnection((result) => {
          if (!result.connected) return;
          this.mongoCollections.set(form.collections());
          const metadata = form.getMetadata();
          if (metadata)
            this.provisionDestinationConnection(metadata, () =>
              this._advancePastStep1(),
            );
        });
        return;
      }
      // CSV/Blob (and SQL/Mongo once already probed 'ok'): provision (create/update) the real
      // DestinationConfiguration here, immediately on leaving Configure, then advance once it succeeds.
      // Mongo reaches here when the user already clicked Test Connection manually before Next — the branch
      // above only fires on a stale/idle probe, so this is the other place collections needs copying.
      if (isMongoForm(form)) this.mongoCollections.set(form.collections());
      const metadata = form.getMetadata();
      if (!metadata) return;
      this.provisionDestinationConnection(metadata, () =>
        this._advancePastStep1(),
      );
      return;
    }
    if (this.step() < this.TOTAL_STEPS) {
      // Leaving Data groups (2) always lands on the group-selection screen, never resuming
      // whichever group's canvas was open last time — even if the user had drilled in before.
      if (this.step() === 2) {
        this.selectedGroupForMapping.set(null);
        // Map fields (3) lists groups in the order they should actually be run — a prerequisite
        // resource (e.g. Patient) always before whatever requires it — not raw selection-click order.
        const sorted = sortByDependencyRank(this.selectedResources());
        this.selectedResources.set(sorted);
        console.table(
          sorted.map((resource) => ({
            resource,
            rank: dependencyRankFor(resource),
          })),
        );
      }
      // Leaving Map fields (3) — every resource's mapping is done — log the full, rank-ordered Mapping
      // JSON across every resource that has at least one mapping, so it's there to copy without needing
      // the (hidden-from-this-screen) Save mapping button.
      if (this.step() === 3 && this.canSaveMappingSummary()) {
        const doc = buildMappingSummaryDocument({
          sourceVendor: this.sourceVendor().toUpperCase(),
          destType: this.nonFhirDestType(),
          destLabel: this.destLabel(),
          mappingRows: this.mappingRows(),
          sqlTables: this.sqlTables(),
          childTableRelationsByTable: this.childTableRelationsByTable(),
          availableFields: this.availableFieldsFn,
          sourceConnectionId: this.sourceConnectionId(),
          destinationId:
            this.selectedExistingId() ?? this.resolvedDestinationId(),
          targetByResource: this.targetByResource(),
        });
        console.log(JSON.stringify(doc, null, 2));
      }
      this.step.update((x) => x + 1);
      this._hasProgressed.set(true);
    } else {
      // Nothing hits the real database until this exact moment: every create-table/add-column/drop-column/
      // alter-column queued while mapping runs for real here, in order, before the mapping profile itself
      // is ever saved — see flushPendingSchemaOps.
      this.flushPendingSchemaOps().subscribe((ok) => {
        if (ok) this._save();
      });
    }
  }

  private _advancePastStep1(): void {
    this.step.set(2);
    this._hasProgressed.set(true);
  }

  // ── Step 3: data-group selection screen ────────────────────────────────────
  // Step 3 no longer opens the mapping canvas directly — it first shows a list of the data groups
  // chosen in Step 2, and only opens the (unmodified) canvas, scoped to one resource at a time, once
  // the user clicks "Map" on a row. The canvas's own `resources` input already accepts an arbitrary
  // subset, so scoping it to one group needs no change to the canvas itself.
  private readonly selectedGroupForMapping = signal<string | null>(null);

  // Guards against a stale selection if the user returns to Step 2 and deselects the group they were
  // mapping — falls back to the selection screen rather than showing a canvas for an unselected group.
  readonly activeMappingGroup = computed(() => {
    const g = this.selectedGroupForMapping();
    return g && this.resourceKeys().includes(g) ? g : null;
  });

  // Snapshot of mappingRows/targetByResource taken the moment a group's canvas opens, so "Close" can
  // discard whatever changed during this session (confirmed first) while "Save" just keeps it.
  private mappingRowsSnapshot: MappingRow[] | null = null;
  private targetByResourceSnapshot: Record<string, string> | null = null;
  readonly pendingExitConfirm = signal(false);
  /** Non-null while the "save anyway?" confirm dialog is up — see saveGroupMapping/buildParentReferenceWarnings. */
  readonly pendingSaveWarnings = signal<PendingParentReferenceWarning[] | null>(null);
  /** Non-null while the transform-rule-conflict dialog is up — see saveGroupMapping/validateRuleConflictsForSave.
   *  Unlike pendingSaveWarnings this genuinely blocks Save: it's only dismissed by fixing every conflict
   *  (override/bypass) or cancelling back to the canvas, never by a "save anyway". */
  readonly pendingRuleConflicts = signal<PendingTransformRuleConflict[] | null>(null);
  /** Id of whichever conflict row currently has an override/bypass request in flight — disables that row's
   *  buttons so a slow save can't be double-clicked; other rows stay usable. */
  readonly resolvingRuleConflict = signal<string | null>(null);

  /** The real backend DestinationType for whichever destination family this wizard instance is
   *  configuring — same ternary already used inline at every mapping-profiles/import call site
   *  (see e.g. buildMappingSummaryDocument's destinationType), centralized here for the Rules dialog. */
  /** Non-private: the field-mapping canvas's join popover needs this too, to load/save a per-connector
   *  transformation rule with the right DestinationType — same resolution the "Rules" button/modal already
   *  used, just also bound straight into the canvas template now instead of only called from this class. */
  resolveDestinationTypeForRules(): DestinationType {
    if (this.isMongo()) return 'Mongo';
    if (this.isMedplum()) return 'Medplum';
    if (this.isFhir()) return 'FhirRepository';
    if (this.isAzureFhir()) return 'AzureFhirService';
    if (this.isBlob()) return 'BlobStorage';
    if (!this.isSql()) return 'Csv';
    return this.isMySql()
      ? 'MySql'
      : this.isPostgres()
        ? 'PostgreSql'
        : 'SqlServer';
  }

  /** Opens the Rules modal for one resource's already-mapped columns — shows whichever rule is
   *  currently in effect (resolved via the Global/DestinationType/ResourceType/Field/Workflow scope
   *  chain) and lets the user add/edit a field-level override. Unlike "Map", this never mutates
   *  mappingRows() itself, so there's no snapshot/discard-guard needed around it. */
  openRulesForResource(resource: string): void {
    const columns = this.mappingRows()
      .filter((r) => r.resource === resource)
      .map((r) => ({
        tableName: r.tableName,
        targetName: r.targetName,
        sourceField: r.sources[0]?.fhirPath ?? null,
        sourceValueType: r.sources[0]?.valueType ?? null,
      }));

    if (columns.length === 0) {
      this.toast.error(
        `Map at least one field for ${resource} before configuring its transformation rules.`,
      );
      return;
    }

    this.dialogService.open<TransformRulesDialogComponent, TransformRulesDialogData>(
      TransformRulesDialogComponent,
      {
        width: '680px',
        // Opts into the shell's own maximize toggle — see TransformRulesDialogComponent's own
        // scss/html for how it now fills whatever size the shell's panel gives it, in either state.
        maximizable: true,
        data: {
          resourceType: resource,
          destinationType: this.resolveDestinationTypeForRules(),
          sourceSystem: this.sourceVendor() || null,
          columns,
        },
      },
    );
  }

  /** "Select Existing" is only offered once a real sourceConnectionId, destinationId, and vendor are all
   *  known — i.e. the source was picked via "Existing Epic Connection" rather than typed in fresh (a brand-new
   *  inline source has no sourceConnectionId until the whole workflow is built; see wizard.service.ts's save()).
   *  Destination is real by this point in virtually every case, since provisionDestinationConnection() runs
   *  right after Step 1. */
  readonly canSelectExistingProfile = computed(
    () =>
      !!this.sourceConnectionId() &&
      !!(this.selectedExistingId() ?? this.resolvedDestinationId()) &&
      !!this.sourceVendor(),
  );

  openExistingProfilePicker(resource: string): void {
    const sourceConnectionId = this.sourceConnectionId();
    const destinationId =
      this.selectedExistingId() ?? this.resolvedDestinationId();
    if (!sourceConnectionId || !destinationId) return;

    this.dialogService
      .open<
        ExistingMappingProfileDialogComponent,
        ExistingMappingProfileDialogData,
        MappingProfileDto | null
      >(ExistingMappingProfileDialogComponent, {
        width: '640px',
        data: { resourceType: resource, sourceConnectionId, destinationId },
      })
      .afterClosed()
      .subscribe((profile) => {
        if (profile) this._applyExistingProfile(resource, profile);
      });
  }

  /** "Mark as Master" for whichever resource's mapping canvas is currently open — promotes the mapping
   *  profile this Field Mapping node already saved for that resource into a new, independently-named master
   *  template (see MappingProfileService.promoteToMaster). Requires the mapping to have been saved at least
   *  once already (so a real profile id exists to clone from); the button stays enabled regardless, but this
   *  guards with a clear toast rather than silently no-op-ing. */
  markActiveGroupAsMaster(): void {
    const resource = this.activeMappingGroup();
    if (!resource) return;

    const mappingNode = this.pipelineStore.byId(this.attachNode().id);
    const existingIds = this._parseExistingMappingProfileIds(
      mappingNode?.fields ?? {},
    );
    const profileId = existingIds[resource];
    if (!profileId) {
      this.toast.show(
        'Save the mapping first',
        `Save "${resource}"'s mapping at least once before marking it as a master template.`,
      );
      return;
    }

    this.dialogService
      .open<
        PromoteMappingProfileDialogComponent,
        PromoteMappingProfileDialogData,
        string | null
      >(PromoteMappingProfileDialogComponent, {
        width: '480px',
        data: {
          resourceType: resource,
          suggestedName: `${resource} — ${this.destLabel()}`,
        },
      })
      .afterClosed()
      .subscribe((name) => {
        if (!name) return;
        this.mappingProfileSvc.promoteToMaster(profileId, name).subscribe({
          next: () =>
            this.toast.success(
              'Master mapping saved',
              `"${name}" is now available via "Select Existing".`,
            ),
          error: (err) => {
            const msg =
              err?.error?.title ??
              err?.error?.error ??
              err?.message ??
              'Failed to save the master mapping.';
            this.toast.show(
              'Master mapping not saved',
              typeof msg === 'string'
                ? msg
                : 'Failed to save the master mapping.',
            );
          },
        });
      });
  }

  /** MappingProfile.DestinationObject is stored as the bare table name (e.g. "Patient_NewMapped"), but
   *  targetCards() in the canvas only renders a target card when targetFor(resource) matches an entry in
   *  sqlTableOptions() — which holds the schema-qualified fullName (e.g. "dbo.Patient_NewMapped"). Applying
   *  the bare name straight through silently drops the target card (canvas renders with nothing on it,
   *  even though mappingRows() is populated) rather than erroring, so this resolves it against the live
   *  probed schema first. Returns null (caller warns) when the destination's schema wasn't probed with a
   *  matching table at all — a real "the two don't line up" case, not just a naming quirk. */
  private _resolveDestinationObjectForCanvas(
    destinationObject: string,
  ): string | null {
    if (!this.isSql()) return destinationObject; // CSV/Mongo targets are never schema-qualified
    const table = this.sqlTables().find(
      (t) =>
        t.fullName.toLowerCase() === destinationObject.toLowerCase() ||
        t.tableName.toLowerCase() === destinationObject.toLowerCase(),
    );
    return table?.fullName ?? null;
  }

  /** MappingFieldDto carries jsonPath, not the canvas's fhirPath — and the canvas's wire overlay
   *  (field-mapping-wires.component.ts's sourceAnchorPoint) looks up the source tree anchor by fhirPath
   *  exactly, in the "${resource}.${relPath}" form buildForest() assigns each leaf (see
   *  field-mapping-tree.util.ts). A guessed fhirPath (e.g. stripping "$." off jsonPath) essentially never
   *  matches that id — the column still renders "mapped" (driven by mappingRows/targetName matching, not
   *  by the wire), but no connector line draws, which reads as broken even though the mapping itself is
   *  intact. Resolving against the same FHIR catalog the rest of the wizard already uses (availableFields)
   *  gets the real fhirPath — matched by jsonPath first (exact, since both come from the same catalog),
   *  falling back to the destination column name for the rare field the catalog doesn't carry. */
  private _resolveFhirPath(
    resource: string,
    f: MappingFieldDto,
  ): { fhirPath: string; label: string; arrays?: string[] } {
    const catalog = this.availableFields(resource);
    const byJsonPath = f.jsonPath
      ? catalog.find((c) => c.jsonPath === f.jsonPath)
      : undefined;
    const byColumnName =
      byJsonPath ??
      catalog.find(
        (c) =>
          c.sqlColumn.toLowerCase() === f.targetField.toLowerCase() ||
          c.csvColumn.toLowerCase() === f.targetField.toLowerCase(),
      );
    if (byColumnName)
      return {
        fhirPath: byColumnName.path,
        label: byColumnName.label,
        arrays: byColumnName.arrays,
      };
    return { fhirPath: `${resource}.${f.targetField}`, label: f.targetField };
  }

  /** Replaces one resource's mapping rows and target with an existing MappingProfile's saved fields —
   *  every other resource's rows are left untouched. */
  private _applyExistingProfile(
    resource: string,
    profile: MappingProfileDto,
  ): void {
    const resolvedTarget = this._resolveDestinationObjectForCanvas(
      profile.destinationObject,
    );
    if (!resolvedTarget) {
      this.toast.error(
        'Target table not found',
        `"${profile.destinationObject}" isn't in this destination's probed schema — the fields were not applied. Probe the schema (or pick the right table) first.`,
      );
      return;
    }

    const newRows: MappingRow[] = profile.fields.map((f) => {
      const resolved = this._resolveFhirPath(resource, f);
      return {
        resource,
        sources: [
          {
            fhirPath: resolved.fhirPath,
            label: resolved.label,
            jsonPath: f.jsonPath,
            valueType: f.valueType,
            arrays: resolved.arrays,
          },
        ],
        mode: 'value',
        instance:
          f.arrayPolicy === 'RepeatParent'
            ? { type: 'all', aggregate: 'rows' }
            : { type: 'first' },
        targetName: f.targetField,
        tableName: resolvedTarget,
        isRequired: f.isRequired,
        defaultValue: f.defaultValue ?? null,
        format: f.format ?? null,
        isUpsertKey: f.isUpsertKey ?? false,
      };
    });

    this.mappingRows.update((rows) => [
      ...rows.filter((r) => r.resource !== resource),
      ...newRows,
    ]);
    this.targetByResource.update((m) => ({ ...m, [resource]: resolvedTarget }));
    this.toast.success(
      'Mapping profile applied',
      `Loaded "${profile.name}" for ${resource}.`,
    );
  }

  openGroupMapping(resource: string): void {
    this.mappingRowsSnapshot = structuredClone(this.mappingRows());
    this.targetByResourceSnapshot = structuredClone(this.targetByResource());
    this.selectedGroupForMapping.set(resource);
  }

  private closeGroupMapping(): void {
    this.mappingRowsSnapshot = null;
    this.targetByResourceSnapshot = null;
    this.selectedGroupForMapping.set(null);
  }

  /** "Save" below the canvas — keeps whatever's mapped so far, returns to the group list, and shows the
   *  canonical Mapping JSON built from every resource mapped so far (not just this one). Blocked by
   *  validateMappingForSave: the canvas stays open (never closeGroupMapping()s) until every error for
   *  this resource is fixed, so nothing wrong ever actually gets persisted. This is the ONLY caller of
   *  validateMappingForSave — the Review step's "Add to Workflow" (next()/buildWorkflow) intentionally
   *  never re-runs it; by the time a resource's mapping reaches Review it has already been through this
   *  gate, so Add to Workflow's job is just provisioning/saving the already-valid configuration. */
  saveGroupMapping(): void {
    const group = this.activeMappingGroup();
    if (!group) return;

    // A rule's declared output type (if any) must win over the raw source type in checkColumnTypeCompatibility
    // below — see resolveRuleExpectedTypesForSave's own doc comment — so that lookup has to resolve before
    // validateMappingForSave runs, not after (validateRuleConflictsForSave, further down, is a separate/later
    // check and doesn't help here: it flags a MISMATCHED rule type, it doesn't supersede the raw-type check).
    this.resolveRuleExpectedTypesForSave(group).subscribe((ruleExpectedTypeByField) => {
      const errors = this.validateMappingForSave(group, ruleExpectedTypeByField);
      if (errors.length > 0) {
        this.toast.error(
          `Fix ${errors.length} mapping issue${errors.length === 1 ? '' : 's'} before saving`,
          errors.join(' '),
        );
        return;
      }

      this.validateRuleConflictsForSave(group).subscribe((conflicts) => {
        if (conflicts.length > 0) {
          this.pendingRuleConflicts.set(conflicts);
          return;
        }
        // Soft checks (currently just a missing required parent reference — see buildParentReferenceWarnings)
        // don't block Save the way validateMappingForSave's errors do: the user may fully intend to wire the
        // parent resource's mapping in another group, or resolve it later, so they're shown a
        // confirm-before-proceed dialog instead (see pendingSaveWarnings) rather than forcing a fix right now.
        const warnings = this.buildParentReferenceWarnings(group);
        if (warnings.length > 0) {
          this.pendingSaveWarnings.set(warnings);
          return;
        }
        this.completeSaveGroupMapping(group);
      });
    });
  }

  /** User chose "Save anyway" on the pendingSaveWarnings dialog, leaving whatever's still unresolved. */
  confirmSaveWithWarnings(): void {
    const group = this.activeMappingGroup();
    this.pendingSaveWarnings.set(null);
    if (group) this.completeSaveGroupMapping(group);
  }

  /** User chose "Keep editing" on the pendingSaveWarnings dialog — nothing is saved. */
  cancelSaveWithWarnings(): void {
    this.pendingSaveWarnings.set(null);
  }

  onSaveWarningsBackdropClick(e: MouseEvent): void {
    if (e.target === e.currentTarget) this.cancelSaveWithWarnings();
  }

  /** The dialog's one-click fix for a warning that has a detected source field (see
   *  buildParentReferenceWarnings): re-targets the row already mapping that field if one exists, or
   *  creates a fresh one — either way setting referencesResource so the reference actually resolves.
   *  Re-evaluates warnings for the resource afterward; if none remain, closes the dialog and returns the
   *  user to the canvas to review the fix rather than saving for them — they still click Save themselves
   *  when ready (same as if there'd been no warning at all). If other warnings remain, the dialog just
   *  updates to show those. */
  resolvePendingReferenceWarning(
    w: PendingParentReferenceWarning,
    destinationColumn: string,
  ): void {
    if (!w.sourceFieldPath) return;

    if (!destinationColumn) {
      this.toast.error(
        'Select a destination column',
        `Choose which column on ${this.targetFor(w.resource)} should receive "${w.sourceFieldLabel}" before mapping it.`,
      );
      return;
    }

    const rows = this.mappingRows();
    const idx = rows.findIndex(
      (r) => r.resource === w.resource && r.sources[0]?.fhirPath === w.sourceFieldPath,
    );
    const tableName = this.targetFor(w.resource);
    const collidesWithOtherRow = rows.some(
      (r, i) => i !== idx && r.resource === w.resource && r.tableName === tableName && r.targetName === destinationColumn,
    );
    if (collidesWithOtherRow) {
      this.toast.error(
        'Column already mapped',
        `"${destinationColumn}" on ${tableName} is already mapped from another field — pick a different column, or remove that mapping first.`,
      );
      return;
    }

    this.mappingRows.update((rows) => {
      if (idx >= 0) {
        const updated = [...rows];
        updated[idx] = { ...updated[idx], targetName: destinationColumn, referencesResource: w.parent };
        return updated;
      }
      const newRow: MappingRow = {
        resource: w.resource,
        sources: [{
          fhirPath: w.sourceFieldPath!,
          label: w.sourceFieldLabel ?? w.sourceFieldPath!,
          jsonPath: w.sourceFieldJsonPath,
          valueType: w.sourceFieldValueType,
          arrays: w.sourceFieldArrays,
        }],
        mode: 'value',
        instance: { type: 'first' },
        targetName: destinationColumn,
        tableName: this.targetFor(w.resource),
        referencesResource: w.parent,
      };
      return [...rows, newRow];
    });

    const remaining = this.buildParentReferenceWarnings(w.resource);
    this.pendingSaveWarnings.set(remaining.length === 0 ? null : remaining);
    if (remaining.length === 0) {
      this.toast.success(
        'Reference resolved',
        `"${w.sourceFieldLabel}" now resolves to "${w.parent}" — review the mapping, then click Save.`,
      );
    }
  }

  private completeSaveGroupMapping(group: string): void {
    this.closeGroupMapping();
    if (this.canSaveMappingSummary()) this.buildAndShowMappingSummary();
    else
      this.toast.success('Mapping saved', `${group} mapping progress saved.`);
  }

  /** Catches mappings that would silently write wrong or lost data rather than letting them through
   *  unnoticed — checked right before "Save" is allowed to actually persist anything (see
   *  saveGroupMapping). Table/column-existence checks are skipped entirely when there's no live SQL
   *  schema to check against (CSV, or SQL not yet connected) — a free-text column is always valid there. */
  private validateMappingForSave(resource: string, ruleExpectedTypeByField: Map<string, string | null>): string[] {
    const errors: string[] = [];

    // A still-queued "add column" whose name collides with a column the live probe already found on
    // that table (origin 'probed' — it predates anything queued via the canvas's own "+ Add column"
    // this session) — the backend rejects this the moment "Add to Workflow" flushes the queue for real
    // ("Column names in each table must be unique..."), several steps after the mapping canvas that
    // queued it. Checked here instead, so it's caught on this canvas's own Save — regardless of whether
    // this resource has any mapped rows yet, hence ahead of the early-return below.
    for (const op of this.pendingSchemaOps()) {
      if (op.kind !== 'addColumn') continue;
      const table = this.sqlTables().find(
        (t) => t.fullName === op.request.tableName,
      );
      const collides = table?.columns.some(
        (c) =>
          c.origin === 'probed' &&
          c.name.toLowerCase() === op.request.columnName.toLowerCase(),
      );
      if (collides) {
        errors.push(
          `"${op.request.columnName}" already exists on ${op.request.tableName} — remove the queued "Add column" and map to the existing column instead.`,
        );
      }
    }

    const rows = this.mappingRows().filter((r) => r.resource === resource);
    if (rows.length === 0) return errors; // nothing else mapped yet isn't itself an error — Save just no-ops.

    // Mirrors WorkflowBuildAssemblerService.buildMappingForResource's own "Upsert/Update with no id
    // column mapped" check — that one only ever ran once "Add to Workflow" assembled the full build
    // request, several steps after the mapping canvas that's actually missing the id column. The
    // destination's write mode was already committed in Step 1 (dest_writeMode) — read the same way the
    // canvas's own connectionInfo/csvDelimiterKey bindings already read config from that step. Deliberately
    // AFTER the rows.length===0 early-return above, not ahead of it — a resource with nothing mapped at all
    // (including one that just had its only mapped table/field removed) isn't a broken mapping, it's an
    // unstarted one, same as every other check below; this must not be the one exception that blocks Save
    // on an empty canvas.
    const writeMode = this.activeFormConfig()['dest_writeMode'];
    if (writeMode === 'upsert' || writeMode === 'update') {
      const hasIdMapping = rows.some(
        (r) => r.isUpsertKey || r.sources[0]?.fhirPath === `${resource}.id`,
      );
      if (!hasIdMapping) {
        const modeLabel =
          writeMode === 'upsert' ? 'Upsert by source id' : 'Update only';
        errors.push(
          `"${resource}" destination is set to ${modeLabel}, but no destination column is mapped from ` +
            `${resource}.id. Map the resource's id field to a column, or switch Write mode to Insert only.`,
        );
      }
    }

    const knownTables = this.hasSqlTables()
      ? new Set([
          ...this.sqlTableOptions(),
          // MySQL-only bare-name fallback — see FieldMappingCanvasComponent.sqlTableNames' doc comment.
          // A saved/reopened mapping's row.tableName is always the bare name for MySQL (bareName(),
          // field-mapping-summary.model.ts), while sqlTableOptions() is the live-probed, database-qualified
          // name — without this, a perfectly valid restored MySQL mapping fails Save with "no longer exists
          // in the destination database" even though the table/columns render correctly (isPrimaryTargetValid/
          // columnsForTableFn already tolerate this same mismatch). SQL Server/PostgreSQL keep strict
          // fullName-only matching since their dbo./public. qualification genuinely matches the live probe.
          ...(this.isMySql() ? this.sqlTableBareNames() : []),
        ])
      : null;

    const targetCounts = new Map<string, number>();

    for (const row of rows) {
      if (row.mode === 'value' && row.sources.length === 0) {
        errors.push(
          `"${row.targetName}" on ${row.tableName} has no source field selected.`,
        );
        continue;
      }

      if (knownTables && !knownTables.has(row.tableName)) {
        errors.push(
          `${row.tableName} no longer exists in the destination database — remove or retarget "${row.targetName}".`,
        );
        continue;
      }

      // Belt-and-suspenders for a row wired before FieldMappingCanvasComponent.completeMapping started
      // guarding against these (or restored from an older/legacy snapshot saved before that guard
      // existed) — the column's value is filled in automatically regardless (by the database for an
      // identity/computed column, by the mapping engine's own child-table relationship resolution for a
      // foreign key), so a direct write to it is never valid; catch it here instead of leaving that as
      // the user's first signal, several steps after the mapping canvas that let it happen.
      if (knownTables) {
        const table = this.sqlTables().find(
          (t) =>
            t.fullName === row.tableName ||
            // MySQL-only bare-name fallback — see knownTables' own comment above.
            (this.isMySql() && t.tableName === row.tableName),
        );
        const column = table?.columns.find((c) => c.name === row.targetName);
        if (column?.isAutoGenerated) {
          errors.push(
            `"${row.targetName}" is an identity or computed column in ${row.tableName} and cannot be a mapping write target — remove or retarget this field.`,
          );
          continue;
        }
        if (column?.isForeignKey) {
          errors.push(
            `"${row.targetName}" is a foreign key on ${row.tableName}${column.references ? ` (→ ${column.references})` : ''} — it's populated automatically from that relationship and cannot be a mapping write target. Remove or retarget this field.`,
          );
          continue;
        }

        // See checkColumnTypeCompatibility's own doc comment (field-mapping-model.ts) — mirrors
        // CreateMappingProfileRequestValidator.ValidateAgainstDestinationSchemaAsync (the backend's
        // /workflows/build validator), catching a real type mismatch on this canvas's own Save instead of
        // only surfacing once the whole workflow is saved. Covers childJson rows too (effective ValueType
        // 'Json'), not just 'value' rows — a childJson row used to be exempt here on the assumption that
        // "always written as JSON text" meant always valid, which is true of the write mechanics but not
        // of whether the destination column accepts Json at all (e.g. a plain varchar column doesn't).
        // A transformation rule already resolved for this exact connector (see
        // resolveRuleExpectedTypesForSave) overrides the raw source-type comparison — its declared output
        // type is what actually reaches the column at runtime, not birthDate's own Date type, e.g.
        const ruleKey = `${row.tableName}::${row.targetName}`;
        const ruleExpectedType = ruleExpectedTypeByField.has(ruleKey)
          ? ruleExpectedTypeByField.get(ruleKey)!
          : undefined;
        const typeError = checkColumnTypeCompatibility(row, column, ruleExpectedType);
        if (typeError) {
          errors.push(typeError);
          continue;
        }
      }

      if (
        knownTables &&
        !this.columnsForTableFn(row.tableName).includes(row.targetName)
      ) {
        errors.push(
          `"${row.targetName}" no longer exists in ${row.tableName}'s columns.`,
        );
        continue;
      }

      const key = `${row.tableName}::${row.targetName}`;
      targetCounts.set(key, (targetCounts.get(key) ?? 0) + 1);
    }

    for (const [key, count] of targetCounts) {
      if (count <= 1) continue;
      const [tableName, targetName] = key.split('::');
      errors.push(
        `${tableName}.${targetName} is mapped ${count} times — only one mapping would actually be written.`,
      );
    }

    return errors;
  }

  /** Feeds checkColumnTypeCompatibility's rule-aware branch (field-mapping-model.ts) — one getEffectiveRules
   *  call per mapped 'value' row of this resource, reduced to just what that check needs: whether a rule is
   *  already resolved for this exact connector, and if so, its declared expectedValueType. Map key matches
   *  validateMappingForSave's own targetCounts key ("tableName::targetName"); a present-but-null value means
   *  "a rule exists here but declares no output type" (skip the type check, trust the rule) — distinct from
   *  a missing key, which means "no rule at all" (fall back to comparing the raw source type). Same
   *  hasSqlTables()/destinationType short-circuit as validateRuleConflictsForSave below, which this always
   *  runs directly ahead of. */
  private resolveRuleExpectedTypesForSave(resource: string): Observable<Map<string, string | null>> {
    if (!this.hasSqlTables()) return of(new Map());

    const destinationType = this.resolveDestinationTypeForRules();
    if (!destinationType) return of(new Map());

    const rows = this.mappingRows().filter((r) => r.resource === resource && r.mode === 'value');
    if (rows.length === 0) return of(new Map());

    const resourcePipelineRouteId = this.currentWorkflowId() ?? undefined;
    const sourceSystem = this.sourceVendor() || null;

    const checks = rows.map((row) => {
      const sourceField = row.sources[0]?.fhirPath ?? null;
      return this.transformationRulesSvc
        .getEffectiveRules({
          destinationType,
          resourceType: resource,
          destinationField: row.targetName,
          resourcePipelineRouteId,
          sourceSystem,
          sourceField,
        })
        .pipe(
          map((rules): [string, string | null] | null =>
            rules.length === 0 ? null : [`${row.tableName}::${row.targetName}`, rules[0].expectedValueType ?? null],
          ),
          // A transient rule-lookup failure shouldn't block Save on its own here either — same tolerance
          // validateRuleConflictsForSave already uses; the server-side check is still the backstop.
          catchError(() => of(null)),
        );
    });

    return forkJoin(checks).pipe(
      map((entries) => new Map(entries.filter((e): e is [string, string | null] => e !== null))),
    );
  }

  /** Mirrors CreateMappingProfileRequestValidator.ValidateApplicableRulesAsync (the backend's mapping-profile
   *  save gate) client-side: a field can pass every check in validateMappingForSave above — the mapped value
   *  itself matches the column's type — and still fail at pipeline-run time because a Global/ResourceType/
   *  DestinationType-scoped TransformationRule applies to it and expects a different type than the column
   *  actually is (e.g. a Global NumberCast rule hitting a text column). Async because it needs one
   *  getEffectiveRules call per mapped field; returns [] immediately (no network calls) when there's no live
   *  SQL schema to check column types against, same short-circuit validateMappingForSave uses. */
  private validateRuleConflictsForSave(resource: string): Observable<PendingTransformRuleConflict[]> {
    if (!this.hasSqlTables()) return of([]);

    const destinationType = this.resolveDestinationTypeForRules();
    if (!destinationType) return of([]);

    const rows = this
      .mappingRows()
      .filter((r) => r.resource === resource && r.mode === 'value');
    if (rows.length === 0) return of([]);

    // Resolved with the real workflow id when editing an already-saved workflow, so an override already
    // added for this workflow (see resolveRuleConflictOverride/resolveRuleConflictBypass) suppresses the
    // broader-tier rule here exactly the way it will at pipeline-run time — see EffectiveRuleResolver.
    // Null while still building a brand-new workflow (no ResourcePipelineRouteId exists yet).
    const resourcePipelineRouteId = this.currentWorkflowId() ?? undefined;

    const checks = rows.map((row) => {
      const table = this.sqlTables().find((t) => t.fullName === row.tableName);
      const column = table?.columns.find((c) => c.name === row.targetName);
      if (!column?.mappingValueType) return of([] as PendingTransformRuleConflict[]);

      const sourceSystem = this.sourceVendor() || null;
      const sourceField = row.sources[0]?.fhirPath ?? null;

      return this.transformationRulesSvc
        .getEffectiveRules({
          destinationType,
          resourceType: resource,
          destinationField: row.targetName,
          resourcePipelineRouteId,
          sourceSystem,
          sourceField,
        })
        .pipe(
          map((rules) =>
            rules
              .filter(
                (rule) =>
                  rule.expectedValueType &&
                  rule.expectedValueType.toLowerCase() !== column.mappingValueType.toLowerCase(),
              )
              .map(
                (rule): PendingTransformRuleConflict => ({
                  resource,
                  tableName: row.tableName,
                  targetName: row.targetName,
                  destinationType,
                  columnValueType: column.mappingValueType,
                  columnDataType: column.dataType,
                  sourceSystem,
                  sourceField,
                  rule,
                  message:
                    `A ${rule.scope} rule (${rule.nodeType}) expects "${row.targetName}" on ${row.tableName} to be ` +
                    `${rule.expectedValueType}, but it's a ${column.dataType} column (${column.mappingValueType}).`,
                }),
              ),
          ),
          catchError(() => of([] as PendingTransformRuleConflict[])), // A transient rule-lookup failure shouldn't block Save on its own — the server-side check is still the backstop.
        );
    });

    return forkJoin(checks).pipe(map((results) => results.flat()));
  }

  /** Stable per-row key for the conflicts dialog's @for track and resolvingRuleConflict guard — a rule can
   *  appear more than once across fields, so id alone isn't unique to a row. */
  ruleConflictKey(c: PendingTransformRuleConflict): string {
    return `${c.tableName}::${c.targetName}::${c.rule.id}`;
  }

  /** User cancelled the transform-rule-conflict dialog — back to the canvas, nothing saved. */
  cancelRuleConflicts(): void {
    this.pendingRuleConflicts.set(null);
  }

  onRuleConflictsBackdropClick(e: MouseEvent): void {
    if (e.target === e.currentTarget) this.cancelRuleConflicts();
  }

  /** "Override" — adds a Workflow-scoped TransformationRule that clones the conflicting rule's transform
   *  behavior (same node type/config/null-handling) but declares the destination column's own type as its
   *  expectedValueType, so the field keeps being transformed the same way while no longer conflicting for
   *  THIS workflow. The original rule (whatever broader tier it lives at) is never modified — this is a new
   *  row that simply outranks it per EffectiveRuleResolver's Workflow-first resolution. */
  resolveRuleConflictOverride(c: PendingTransformRuleConflict): void {
    const workflowId = this.currentWorkflowId();
    if (!workflowId) return;
    const key = this.ruleConflictKey(c);
    this.resolvingRuleConflict.set(key);
    const r = c.rule;
    this.transformationRulesSvc
      .save({
        scope: 'Workflow',
        nodeType: r.nodeType,
        config: r.config,
        destinationType: c.destinationType,
        resourceType: c.resource,
        destinationField: c.targetName,
        resourcePipelineRouteId: workflowId,
        sourceSystem: c.sourceSystem,
        sourceField: c.sourceField,
        order: r.order,
        onNull: r.onNull,
        errorPolicy: r.errorPolicy,
        isEnabled: true,
        onNullDefaultValue: r.onNullDefaultValue,
        arrayMode: r.arrayMode,
        fhirWriteBackJsonPath: r.fhirWriteBackJsonPath,
        expectedValueType: c.columnValueType as MappingValueType,
      })
      .subscribe({
        next: () => this.onRuleConflictResolved(c, key, 'Rule overridden for this workflow'),
        error: () =>
          this.onRuleConflictResolveFailed(key, 'Could not add the workflow override — try again.'),
      });
  }

  /** "Bypass" — adds a disabled Workflow-scoped TransformationRule for this exact field. A Workflow-tier row
   *  wins the tier the moment one exists (see EffectiveRuleResolver), even disabled, so this suppresses the
   *  broader-tier rule for this field on this workflow entirely (no transform applied) without touching or
   *  deleting the original rule anywhere else it still applies. */
  resolveRuleConflictBypass(c: PendingTransformRuleConflict): void {
    const workflowId = this.currentWorkflowId();
    if (!workflowId) return;
    const key = this.ruleConflictKey(c);
    this.resolvingRuleConflict.set(key);
    const r = c.rule;
    this.transformationRulesSvc
      .save({
        scope: 'Workflow',
        nodeType: r.nodeType,
        config: r.config,
        destinationType: c.destinationType,
        resourceType: c.resource,
        destinationField: c.targetName,
        resourcePipelineRouteId: workflowId,
        sourceSystem: c.sourceSystem,
        sourceField: c.sourceField,
        order: r.order,
        onNull: r.onNull,
        errorPolicy: r.errorPolicy,
        isEnabled: false,
        expectedValueType: r.expectedValueType,
      })
      .subscribe({
        next: () => this.onRuleConflictResolved(c, key, 'Rule bypassed for this workflow'),
        error: () =>
          this.onRuleConflictResolveFailed(key, 'Could not bypass the rule — try again.'),
      });
  }

  private onRuleConflictResolved(c: PendingTransformRuleConflict, key: string, successTitle: string): void {
    this.resolvingRuleConflict.set(null);
    const remaining = (this.pendingRuleConflicts() ?? []).filter((x) => this.ruleConflictKey(x) !== key);
    this.pendingRuleConflicts.set(remaining.length === 0 ? null : remaining);
    this.toast.success(successTitle, `"${c.targetName}" on ${c.tableName} no longer conflicts.`);
    if (remaining.length === 0) {
      // Every conflict is resolved — re-run Save so the fixed mapping actually persists instead of leaving
      // the user to click Save again themselves.
      this.saveGroupMapping();
    }
  }

  private onRuleConflictResolveFailed(key: string, message: string): void {
    this.resolvingRuleConflict.set(null);
    this.toast.error('Save failed', message);
  }

  /** Soft, confirm-before-proceed checks shown via pendingSaveWarnings — distinct from
   *  validateMappingForSave's errors, which block Save outright. A resource with a "required" parent per
   *  RESOURCE_DEPENDENCIES (e.g. Observation → Patient) needs some mapped field pointed at that parent via
   *  the target card / list's "which resource does this reference?" picker (referencesResource), or the
   *  destination row has no link back to its parent at all. Resource selection only ever *hints*
   *  required/recommended companions (see recommendedResources below) — it never blocks or auto-wires the
   *  reference itself — so this is the only place a missing required link is ever surfaced. Not a hard
   *  block: the user may still intend to wire it via another group's canvas, or accept the gap knowingly.
   *  Relational-only: referencesResource resolves to a real FK lookup against another mapped resource's
   *  table (see WorkflowBuildAssemblerService.resolveReferenceLookup), which is meaningless without a live
   *  SQL schema to resolve against.
   *
   *  Beyond just describing the gap, this resolves an actual fix where it can: the catalog's
   *  referenceTargetTypes (see ResourceFieldDef) identifies which field on `resource` could BE the
   *  reference (e.g. "Subject › Reference" targets Patient/Group/Device/Location) — mirroring
   *  MappingCatalogService.resolveParentReferenceField's own tie-break (fewest target types, then
   *  path) for picking among multiple candidates. If the user already mapped that exact field (just
   *  never set "Resolves to"), that row is offered as the one-click fix; otherwise a fresh mapping is
   *  offered, defaulting to the resource's first destination column. */
  private buildParentReferenceWarnings(resource: string): PendingParentReferenceWarning[] {
    const knownTables = this.hasSqlTables()
      ? new Set(this.sqlTableOptions())
      : null;
    if (!knownTables) return [];

    const rows = this.mappingRows().filter((r) => r.resource === resource);
    if (rows.length === 0) return [];

    const selectedResources = new Set(this.selectedResources());
    const warnings: PendingParentReferenceWarning[] = [];

    for (const parent of requiredClosureFor(resource)) {
      if (rows.some((r) => r.referencesResource === parent)) continue; // already satisfied

      if (!selectedResources.has(parent)) {
        warnings.push({
          resource, parent,
          message: `"${resource}" requires a reference to "${parent}", but "${parent}" isn't selected as a data group in this workflow — add it in the previous step.`,
          sourceFieldPath: null, sourceFieldLabel: null, existingRow: null,
          destinationColumns: [], selectedDestinationColumn: null,
        });
        continue;
      }

      // referenceTargetTypes is Firely-model metadata the real backend catalog carries — a pasted "Load
      // JSON payload" or the built-in fallback defs (both of which availableFields() prefers when
      // present) never have it, since neither has any notion of the FHIR spec's Reference semantics.
      // The catalog fetch (see the effect populating catalogByResource) runs unconditionally for every
      // selected resource regardless of what the visible tree is sourced from, so it's used here
      // directly instead of availableFields() — falling back to that only if the catalog genuinely
      // hasn't loaded (e.g. no backend reachable), in which case there's truly nothing to auto-detect.
      const candidates = (this.catalogByResource()[resource] ?? this.availableFields(resource))
        .filter((f) => (f.referenceTargetTypes ?? []).includes(parent))
        .sort((a, b) =>
          (a.referenceTargetTypes!.length - b.referenceTargetTypes!.length) ||
          a.path.localeCompare(b.path));

      // Prefer whichever candidate the user already mapped (just never resolved) over the "best" one by
      // tie-break rank — the tie-break is only a default for when nothing's mapped yet.
      let chosen = candidates[0] ?? null;
      let existingRow: MappingRow | null = null;
      for (const c of candidates) {
        const row = rows.find((r) => r.sources[0]?.fhirPath === c.path);
        if (row) { chosen = c; existingRow = row; break; }
      }

      if (!chosen) {
        warnings.push({
          resource, parent,
          message: `"${resource}" requires a reference to "${parent}" — map a reference field (e.g. subject) and set its "references" target to "${parent}" so the destination row links back correctly.`,
          sourceFieldPath: null, sourceFieldLabel: null, existingRow: null,
          destinationColumns: [], selectedDestinationColumn: null,
        });
        continue;
      }

      const destinationColumns = this.columnsForResourceTarget(resource);
      warnings.push({
        resource, parent, message: null,
        sourceFieldPath: chosen.path,
        // chosen.label is already the group-then-leaf breadcrumb the source tree itself renders for this
        // field (e.g. "Subject › Reference" — see buildResourceTree in field-mapping-tree.util.ts, whose
        // leaf label is this exact same catalog label). Prefixing the resource name gives the field's full
        // path exactly as it'd read if the tree were expanded all the way to it.
        sourceFieldLabel: `${resource} › ${chosen.label}`,
        sourceFieldJsonPath: chosen.jsonPath,
        sourceFieldValueType: chosen.valueType,
        sourceFieldArrays: chosen.arrays,
        existingRow,
        destinationColumns,
        // Pre-select only when there's already a real choice (the row the user previously mapped) —
        // otherwise leave it blank so picking a column is a deliberate act, not a silent default to
        // whichever happens to be first.
        selectedDestinationColumn: existingRow?.targetName ?? null,
      });
    }
    return warnings;
  }

  /** "Close" below the canvas — always confirms first, since it discards unsaved changes. */
  requestExitMapping(): void {
    this.pendingExitConfirm.set(true);
  }
  cancelExitMapping(): void {
    this.pendingExitConfirm.set(false);
  }

  onExitConfirmBackdropClick(e: MouseEvent): void {
    if (e.target === e.currentTarget) this.cancelExitMapping();
  }

  confirmExitMapping(): void {
    if (this.mappingRowsSnapshot)
      this.mappingRows.set(this.mappingRowsSnapshot);
    if (this.targetByResourceSnapshot)
      this.targetByResource.set(this.targetByResourceSnapshot);
    this.pendingExitConfirm.set(false);
    this.closeGroupMapping();
  }

  back(): void {
    if (this.step() > 1) {
      this.step.update((x) => x - 1);
      // Returning to Configure invalidates a prior probe — force a re-test on the next advance.
      if (this.step() === 1 && this.isSql()) {
        this.probeState.set('idle');
        this.sqlTables.set([]);
        const form = this.activeForm();
        if (isSqlFamilyForm(form)) form.resetProbe();
      }
      if (this.step() === 1 && this.isFhir()) {
        this.probeState.set('idle');
      }
    }
  }

  cancel(): void {
    this.cancelled.emit();
  }

  // ── FHIR (Aidbox) connection test ─────────────────────────────────────────
  // Triggered by the footer's own Next button (see next(), mirroring the SQL-family branch there). FHIR stays
  // hand-rolled (see isFhir()'s doc comment), so it keeps its own test flow instead of going through
  // SqlFamilyFormApi.testConnection(). No tokenEndpoint in the request — for oauth2/clientCredentials the
  // backend discovers it from baseUrl via GET {baseUrl}/.well-known/smart-configuration and returns it as
  // resolvedTokenEndpoint. On success we patch that into this form's (hidden) tokenEndpoint control — so it
  // still lands in dest_tokenEndpoint at save time, exactly as a manually-typed value would have — then
  // provision and advance, folding what used to be two clicks (Test Connection, then Next) into one.
  testFhirConnection(): void {
    const v = this.fhirForm.value;
    this.probeState.set('testing');
    this.probeError.set(null);
    this.schemaSvc
      .testFhir({
        baseUrl: v.baseUrl ?? '',
        authType: v.authType ?? 'oauth2',
        clientId: v.clientId ?? undefined,
        clientSecret: v.clientSecret ?? undefined,
        username: v.username ?? undefined,
        password: v.password ?? undefined,
        bearerToken: v.bearerToken ?? undefined,
      })
      .subscribe({
        next: (res) => {
          if (!res.connected) {
            this.probeState.set('error');
            this.probeError.set(res.error ?? 'Connection failed.');
            return;
          }
          if (res.resolvedTokenEndpoint) {
            this.fhirForm.patchValue({
              tokenEndpoint: res.resolvedTokenEndpoint,
            });
          }
          this.probeState.set('ok');
          if (this.step() < this.TOTAL_STEPS) {
            const metadata = this._getFhirMetadata();
            if (metadata)
              this.provisionDestinationConnection(metadata, () =>
                this._advancePastStep1(),
              );
          }
        },
        error: (err) => {
          this.probeState.set('error');
          this.probeError.set(
            typeof err?.error?.error === 'string'
              ? err.error.error
              : (err?.message ?? 'Connection failed.'),
          );
        },
      });
  }

  // ── select existing connection ───────────────────────────────────────────
  setConnectionMode(mode: 'new' | 'existing'): void {
    this.connectionMode.set(mode);
    if (
      mode === 'existing' &&
      this.existingOptions().length === 0 &&
      !this.existingOptionsLoading()
    ) {
      this._loadExistingOptions();
    }
  }

  /** The "✕" next to the dropdown — undoes a clone and returns the active form to a blank "New" state. This is
   *  the only way back to blank now that there's no explicit New/Existing toggle to switch away from. */
  clearExistingConnection(): void {
    this.connectionMode.set('new');
    this.selectedExistingId.set(null);
    this.selectedDeIdentificationProfileId.set(null);
    this._existingBaseline = null;
    const form = this.activeForm();
    form?.reset();
    if (this.isSql()) {
      this.probeState.set('idle');
      this.sqlTables.set([]);
      if (isSqlFamilyForm(form)) form.resetProbe();
    } else if (this.isFhir()) {
      this.fhirForm.reset();
      this.probeState.set('idle');
      this._syncFhirAuthValidators(this.fhirForm.value.authType ?? null);
    }
  }

  private _loadExistingOptions(): void {
    this.existingOptionsLoading.set(true);
    this.destinationConfigSvc
      .getPaged({ isEnabled: true, page: 1, pageSize: 100 })
      .pipe(
        map((page) => {
          const wantedTypes = this.isSql()
            ? DestinationWizardComponent.SQL_TYPES
            : this.isMongo()
              ? DestinationWizardComponent.MONGO_TYPES
              : this.isMedplum()
                ? DestinationWizardComponent.MEDPLUM_TYPES
                : this.isFhir()
                  ? DestinationWizardComponent.FHIR_TYPES
                  : this.isAzureFhir()
                    ? DestinationWizardComponent.AZUREFHIR_TYPES
                    : this.isBlob()
                      ? DestinationWizardComponent.BLOB_TYPES
                      : DestinationWizardComponent.CSV_TYPES;
          return page.items.filter((item) =>
            wantedTypes.includes(item.destinationType),
          );
        }),
        switchMap((candidates) =>
          candidates.length === 0
            ? of([] as DestinationConfigurationDto[])
            : forkJoin(
                candidates.map((item) =>
                  this.destinationConfigSvc.hasExecutionHistory(item.id).pipe(
                    map((res) => (res.hasExecutionHistory ? null : item)),
                    catchError(() => of(item)),
                  ),
                ),
              ).pipe(
                map((results) =>
                  results.filter(
                    (x): x is DestinationConfigurationDto => x !== null,
                  ),
                ),
              ),
        ),
      )
      .subscribe({
        next: (options) => {
          this.existingOptions.set(options);
          this.existingOptionsLoading.set(false);
        },
        error: () => this.existingOptionsLoading.set(false),
      });
  }

  // Snapshot of the form's raw value taken right after selectExisting() patches it — compared against the current
  // form value at save time (see hasExistingChanged) to decide "reuse as-is" vs "fork a new connection". Secret
  // fields (password/sftpPassword) are part of this snapshot too, at their blank default — a user typing a new
  // secret in counts as a change, same as editing any other field.
  private _existingBaseline: Record<string, unknown> | null = null;

  selectExisting(id: string): void {
    this.connectionMode.set('existing');
    this.selectedExistingId.set(id);
    const selected = this.existingOptions().find((o) => o.id === id);
    if (!selected) return;

    // Read-only here — reusing an existing connection as-is never calls provisionDestinationConnection's
    // create/update branch (see _save()), so there's nothing to change the profile through in this mode.
    this.selectedDeIdentificationProfileId.set(selected.deIdentificationProfileId ?? null);

    const metadata = this._parseConnectionMetadata(
      selected.connectionMetadataJson,
    );
    // dest_name always falls back to the saved record's own name (metadata's dest_name is only ever absent
    // for a record saved before this key existed) — folded into the bag handed to patchFrom so every type's
    // patchFrom sees the same fallback without each needing to special-case it.
    if (!metadata['dest_name']) metadata['dest_name'] = selected.name;

    if (this.isFhir()) {
      this.fhirForm.patchValue({
        name: metadata['dest_name'] || selected.name,
        baseUrl: metadata['dest_baseUrl'] || selected.target || '',
        project: metadata['dest_project'] || '',
        // The backend persists this as dest_fhirAuthType with its own vocabulary (none/bearer/basic/
        // clientCredentials — see buildConnectionMetadata's bridge comment); translate it back to the
        // wizard's own authType values here rather than reading the wrong key.
        authType: this._fhirAuthTypeFromBackend(metadata['dest_fhirAuthType']),
        writeMode: this._fhirWriteModeFromBackend(
          metadata['dest_writeMode'],
          metadata['dest_fhirWriteMode'],
        ),
        tokenEndpoint: metadata['dest_tokenEndpoint'] || '',
        clientId: metadata['dest_clientId'] || '',
        clientSecret: '',
        username: metadata['dest_username'] || '',
        password: '',
        bearerToken: '',
        autoFetchMissingReferences:
          metadata['dest_autoFetchMissingReferences'] === 'true',
        autoFetchMaxCount: metadata['dest_autoFetchMaxCount']
          ? Number(metadata['dest_autoFetchMaxCount'])
          : 25,
      });
      this._syncFhirAuthValidators(this.fhirForm.value.authType ?? null);
      this._existingBaseline = this.fhirForm.getRawValue();
      return;
    }

    const form = this.activeForm();
    if (!form) {
      this._pendingFormPatch.set({
        fields: metadata,
        target: selected.target ?? null,
        isExistingSelection: true,
      });
      return;
    }
    form.patchFrom(metadata, selected.target ?? null);
    this._existingBaseline = form.getRawValue();
  }

  private _parseConnectionMetadata(
    json: string | null | undefined,
  ): Record<string, string> {
    if (!json) return {};
    try {
      const parsed = JSON.parse(json) as unknown;
      return typeof parsed === 'object' && parsed !== null
        ? (parsed as Record<string, string>)
        : {};
    } catch {
      return {};
    }
  }

  /** True once the user has edited any connection field away from what selectExisting() just patched in — the
   *  save-time signal for "fork a new connection" vs "reuse this one untouched" (see _save()). Secret fields
   *  (password/sftpPassword) are excluded on both sides: selectExisting() always leaves them blank (secrets never
   *  come back from the API), so a real password typed in there to satisfy validation — or just out of habit —
   *  must not by itself count as "changed". Reusing as-is never sends whatever was typed there anywhere; forking
   *  (because something ELSE changed) does use it, same as a brand-new connection. */
  hasExistingChanged(): boolean {
    if (!this._existingBaseline) return false;
    const secretKeys = new Set([
      'password',
      'sftpPassword',
      'connectionString',
      'secretValue',
      'clientSecret',
      'bearerToken',
    ]);
    const strip = (v: Record<string, unknown>) =>
      Object.fromEntries(
        Object.entries(v).filter(([key]) => !secretKeys.has(key)),
      );
    const current = this.isFhir()
      ? this.fhirForm.getRawValue()
      : (this.activeForm()?.getRawValue() ?? {});
    return (
      JSON.stringify(strip(current)) !==
      JSON.stringify(strip(this._existingBaseline))
    );
  }

  /** True once Step 1's form has been edited away from its pristine baseline (see _step1Baseline) — the
   *  "there's something here worth confirming before discarding" check for leaving Step 1 *without* ever
   *  advancing past it (_hasProgressed alone misses this: it only flips true once the user clicks Next/Save
   *  — see _advancePastStep1()/_save()). Used alongside _hasProgressed, never instead of it, by both
   *  cancel() here and node-library-dialog's switch-type guard. Secret fields excluded, same rationale and
   *  key list as hasExistingChanged(). */
  isStep1Dirty(): boolean {
    if (!this._step1Baseline) return false;
    const secretKeys = new Set([
      'password',
      'sftpPassword',
      'connectionString',
      'clientSecret',
      'bearerToken',
    ]);
    const strip = (v: Record<string, unknown>) =>
      Object.fromEntries(
        Object.entries(v).filter(([key]) => !secretKeys.has(key)),
      );
    const current = this.isFhir()
      ? this.fhirForm.getRawValue()
      : (this.activeForm()?.getRawValue() ?? {});
    return (
      JSON.stringify(strip(current)) !==
      JSON.stringify(strip(this._step1Baseline))
    );
  }

  /**
   * Resolves a unique name for a forked connection. When the user typed their own distinct name, it's used as-is
   * (forceSuffix false) — only a genuine collision gets a -1/-2/... suffix appended. When the name was left
   * untouched (forceSuffix true, desiredName === the original's own name), a suffix is always appended, since the
   * original itself already holds that exact name.
   */
  private _resolveUniqueName(
    desiredName: string,
    forceSuffix: boolean,
  ): string {
    const taken = new Set(this.existingOptions().map((o) => o.name));
    if (!forceSuffix && !taken.has(desiredName)) return desiredName;
    let suffix = 1;
    let candidate = `${desiredName}-${suffix}`;
    while (taken.has(candidate)) {
      suffix++;
      candidate = `${desiredName}-${suffix}`;
    }
    return candidate;
  }

  /** Reads a Field Mapping node's own previously-saved mappingProfileIds map (falling back to the legacy
   *  singular mappingProfileId, attributed to this node's primary resource, for a node saved before the map
   *  existed) — mirrors WorkflowBuildAssemblerService.parseExistingMappingProfileIds so both save paths agree
   *  on which id belongs to which resource. */
  private _parseExistingMappingProfileIds(
    fields: Record<string, string>,
  ): Record<string, string> {
    const raw = fields['mappingProfileIds'];
    if (raw) {
      try {
        const parsed = JSON.parse(raw) as unknown;
        if (parsed && typeof parsed === 'object')
          return parsed as Record<string, string>;
      } catch {
        // fall through to the legacy singular field below
      }
    }
    const legacyId = fields['mappingProfileId'];
    const primaryResource = this.selectedResources()[0];
    return legacyId && primaryResource ? { [primaryResource]: legacyId } : {};
  }

  hasSqlTables(): boolean {
    return (
      this.isSql() && this.probeState() === 'ok' && this.sqlTables().length > 0
    );
  }

  sqlTableOptions(): string[] {
    return this.sqlTables().map((t) => t.fullName);
  }

  /** Bare table names (no schema/database prefix) of every already-probed SQL table — feeds the canvas's
   *  MySQL-only bare-name fallback (see FieldMappingCanvasComponent.sqlTableNames' doc comment). Harmless
   *  to compute for every dialect; only actually consulted when destType() === 'mysql'. */
  sqlTableBareNames(): string[] {
    return this.sqlTables().map((t) => t.tableName);
  }

  // Columns of the table currently chosen for a resource (drives the target card's own rendering).
  // Deliberately unfiltered — see columnsForTableFn's own doc comment for why.
  columnsForResourceTarget(r: string): string[] {
    const target = this.targetFor(r);
    const table = this.sqlTables().find(
      (t) => t.fullName === target || t.tableName === target,
    );
    return table ? table.columns.map((c) => c.name) : [];
  }

  /** Real data type (e.g. "nvarchar(50)") of one column on any already-known SQL table — undefined for
   *  CSV destinations or free-text/pending columns that have no real schema behind them yet. Purely a
   *  display concern for the mapping canvas's cards; column identity everywhere else stays the name. */
  dataTypeForTableColumn(
    tableFullName: string,
    column: string,
  ): string | undefined {
    const table = this.sqlTables().find(
      (t) => t.fullName === tableFullName || t.tableName === tableFullName,
    );
    return table?.columns.find((c) => c.name === column)?.dataType;
  }

  /** Real PK/FK status of one column on any already-known SQL table (see DestinationColumn.isPrimaryKey/
   *  isForeignKey/references) — undefined for CSV destinations or free-text/pending columns with no real
   *  schema behind them yet. Same display-only role as dataTypeForTableColumn. */
  keyInfoForTableColumn(
    tableFullName: string,
    column: string,
  ): DestinationColumn | undefined {
    const table = this.sqlTables().find(
      (t) => t.fullName === tableFullName || t.tableName === tableFullName,
    );
    return table?.columns.find((c) => c.name === column);
  }

  // ── data groups ───────────────────────────────────────────────────────────
  // Each resource is selected independently — no required/recommended auto-selection or locking. A "recommended"
  // resource (see resource-dependency.config.ts) is surfaced as a dismissible hint below the grid instead — most
  // cross-references are optional at the FHIR level (not every Observation has an Encounter), so nagging via a
  // hard lock would be wrong. Manual selection is the only thing that controls what the backend fetches
  // (SourceNodeExecutors.GetDestinationResourceTypesAsync) — an unselected type referenced by a selected one
  // (Organization, Location, Practitioner, ...) is genuinely left out and can 422 at the destination, so this
  // hint is real guidance worth acting on, not just a nice-to-have.
  isResourceSelected(r: string): boolean {
    return this.selectedResources().includes(r);
  }

  private readonly dismissedRecommendations = signal<ReadonlySet<string>>(
    new Set(),
  );

  /** Recommended-but-not-yet-selected resources across everything currently selected, minus whatever the user
   *  already dismissed this session. Recomputed from scratch on every selection change (not just filtered) so a
   *  dismissed recommendation reappears if it becomes relevant again via a different selected resource. */
  readonly recommendedResources = computed(() => {
    const selected = new Set(this.selectedResources());
    const dismissed = this.dismissedRecommendations();
    const recommended = new Set<string>();
    for (const r of selected) {
      for (const rec of recommendedFor(r)) {
        if (!selected.has(rec) && !dismissed.has(rec)) {
          recommended.add(rec);
        }
      }
    }
    return [...recommended];
  });

  addRecommendedResource(r: string): void {
    this.toggleResource(r);
  }

  /** Applies every current recommendation in one action instead of one chip at a time. Snapshots the list
   *  first — toggleResource mutates selectedResources, which recommendedResources() reads, so iterating the
   *  live computed signal while it's changing would skip/duplicate entries. */
  addAllRecommendedResources(): void {
    const toAdd = this.recommendedResources();
    for (const r of toAdd) {
      this.toggleResource(r);
    }
  }

  dismissRecommendation(r: string): void {
    this.dismissedRecommendations.update((set) => new Set(set).add(r));
  }

  dismissAllRecommendations(): void {
    this.dismissedRecommendations.update(
      (set) => new Set([...set, ...this.recommendedResources()]),
    );
  }

  toggleResource(r: string): void {
    if (this.isResourceSelected(r)) {
      this.selectedResources.update((list) => list.filter((x) => x !== r));
      // Deselecting here only ever shrank dest_resources — mappingRows/targetByResource/extraTablesByGroup/
      // payloadFieldsByResource kept every row this resource ever had, so buildMappingSummaryDocument (which
      // derives its resource list from mappingRows, not selectedResources) and the mapping-profile import it
      // feeds kept saving this resource's mapping to the DB even after unselecting it here. Prune all of it.
      this.mappingRows.update((rows) =>
        rows.filter((row) => row.resource !== r),
      );
      const drop = <T>(m: Record<string, T>): Record<string, T> => {
        const rest = { ...m };
        delete rest[r];
        return rest;
      };
      this.targetByResource.update(drop);
      this.extraTablesByGroup.update(drop);
      this.payloadFieldsByResource.update(drop);
      return;
    }
    this.selectedResources.update((list) => [...list, r]);
  }

  // ── drag-to-reorder the Step 3 data-group rows ──────────────────────────────
  // Plain pointer-capture dragging (no CDK, no native HTML5 DnD) — matches the convention already used
  // everywhere else in this feature (canvas cards, tree-node drag, join-popover header drag). Smoothed
  // with a FLIP animation (capture rects before the reorder, let it happen, then animate FROM the old
  // position TO the new one) since a plain DOM/array reorder otherwise just snaps rows into place —
  // and a midpoint threshold so hovering right at a row's edge doesn't flicker the order back and forth.
  readonly draggingResource = signal<string | null>(null);
  private readonly groupsTableBody =
    viewChild<ElementRef<HTMLElement>>('groupsBody');

  private moveResource(dragged: string, target: string): void {
    if (dragged === target) return;
    this.selectedResources.update((list) => {
      const from = list.indexOf(dragged);
      const to = list.indexOf(target);
      if (from === -1 || to === -1) return list;
      const next = [...list];
      next.splice(from, 1);
      next.splice(to, 0, dragged);
      return next;
    });
  }

  /** Runs `reorder`, then slides every displaced row from its pre-reorder position to its new one
   *  (the classic FLIP technique) instead of letting the browser just snap them into place.
   *
   *  Uses afterNextRender() rather than a raw requestAnimationFrame() to read the "after" positions —
   *  a plain rAF scheduled right after a signal update has NO guaranteed ordering against when Angular
   *  actually commits that update to the DOM (this matters most under zoneless change detection, where
   *  there's no zone.js task-boundary flush to piggyback on). Measuring too early reads the still-old
   *  layout, computes a zero delta for every row, skips the animation entirely, and the reorder then
   *  just snaps into place a frame later — which is exactly "not smooth". afterNextRender() is the
   *  API Angular provides specifically to run code only once a render has actually been committed. */
  private animateReorder(reorder: () => void): void {
    const body = this.groupsTableBody()?.nativeElement;
    if (!body) {
      reorder();
      return;
    }

    const before = new Map<string, DOMRect>();
    body.querySelectorAll<HTMLElement>('[data-resource-row]').forEach((row) => {
      before.set(row.dataset['resourceRow']!, row.getBoundingClientRect());
    });

    reorder();

    afterNextRender(
      () => {
        body
          .querySelectorAll<HTMLElement>('[data-resource-row]')
          .forEach((row) => {
            const oldRect = before.get(row.dataset['resourceRow']!);
            if (!oldRect) return;
            const dy = oldRect.top - row.getBoundingClientRect().top;
            if (!dy) return;
            // Jump back to the old spot with no transition, force a reflow so that's actually painted,
            // then clear the transform on the NEXT frame — the row's own CSS transition animates this
            // last step. (This inner part IS a plain browser paint-cycle concern, not an Angular-render
            // one, so a raw rAF is correct and sufficient here.)
            row.style.transition = 'none';
            row.style.transform = `translateY(${dy}px)`;
            row.getBoundingClientRect();
            requestAnimationFrame(() => {
              row.style.transition = '';
              row.style.transform = '';
            });
          });
      },
      { injector: this.injector },
    );
  }

  onGroupRowDragHandlePointerDown(r: string, ev: PointerEvent): void {
    if (ev.button !== 0) return;
    ev.preventDefault();
    (ev.currentTarget as HTMLElement).setPointerCapture(ev.pointerId);
    this.draggingResource.set(r);
  }

  onGroupRowDragPointerMove(ev: PointerEvent): void {
    const dragged = this.draggingResource();
    if (!dragged) return;
    const overRow = (
      document.elementFromPoint(ev.clientX, ev.clientY) as HTMLElement | null
    )?.closest<HTMLElement>('[data-resource-row]');
    const overResource = overRow?.dataset['resourceRow'];
    if (!overResource || overResource === dragged) return;

    const list = this.selectedResources();
    const movingDown = list.indexOf(dragged) < list.indexOf(overResource);
    const midpoint =
      overRow!.getBoundingClientRect().top +
      overRow!.getBoundingClientRect().height / 2;
    const pastMidpoint = movingDown
      ? ev.clientY > midpoint
      : ev.clientY < midpoint;
    if (!pastMidpoint) return;

    this.animateReorder(() => this.moveResource(dragged, overResource));
  }

  onGroupRowDragPointerUp(ev: PointerEvent): void {
    const el = ev.currentTarget as HTMLElement;
    if (el.hasPointerCapture(ev.pointerId))
      el.releasePointerCapture(ev.pointerId);
    this.draggingResource.set(null);
  }

  // ── per-resource target (file name / table) ────────────────────────────────
  targetFor(r: string): string {
    return this.targetByResource()[r] ?? '';
  }

  updateTarget(r: string, val: string): void {
    this.targetByResource.update((m) => ({ ...m, [r]: val }));
  }

  // ── business-field selection ───────────────────────────────────────────────
  availableFields(r: string): ResourceFieldDef[] {
    // Prefer a pasted real payload for this resource; then the array-aware backend catalog; fall back
    // to the built-in defs until the catalog loads (or if offline).
    return (
      this.payloadFieldsByResource()[r] ??
      this.catalogByResource()[r] ??
      this.defFor(r).fields
    );
  }

  // Stable references for the field-mapping-canvas's function inputs — declared once so the child
  // component doesn't see a new function identity (and re-render) on every change-detection tick.
  readonly availableFieldsFn = (r: string): ResourceFieldDef[] =>
    this.availableFields(r);
  readonly columnsForResourceTargetFn = (r: string): string[] =>
    this.columnsForResourceTarget(r);
  readonly dataTypeForTableColumnFn = (
    tableFullName: string,
    column: string,
  ): string | undefined => this.dataTypeForTableColumn(tableFullName, column);
  readonly keyInfoForTableColumnFn = (
    tableFullName: string,
    column: string,
  ): DestinationColumn | undefined =>
    this.keyInfoForTableColumn(tableFullName, column);

  // ── private ───────────────────────────────────────────────────────────────
  // Last destType _rebuildRows actually ran with — null only before its first call. Lets a SQL-family
  // ⇄ SQL-family switch (see reconcileTargetsForDestTypeSwitch) tell "the destination type just changed"
  // apart from "resources changed" without needing a second effect/signal.
  private _previousDestType: MappingDestType | null = null;

  // Seeds the per-resource target (file name / table) for newly-selected resources
  // and drops rows for resources the user has deselected. Deliberately does NOT
  // auto-populate field rows — the user adds those one at a time via "+".
  private _rebuildRows(resources: string[], type: MappingDestType): void {
    const oldTargets = this.targetByResource();
    let targets = { ...oldTargets };

    // Destination-type switch (e.g. SQL Server -> PostgreSQL): re-qualify any currently-selected
    // resource's target that's still exactly the untouched auto-default under the PREVIOUS type — never
    // a manually typed/renamed/created target, see reconcileTargetsForDestTypeSwitch's own doc comment.
    if (this._previousDestType !== null && this._previousDestType !== type) {
      const catalogSqlTableByResource: Record<string, string> = {};
      for (const r of resources) catalogSqlTableByResource[r] = this.defFor(r).sqlTable;
      targets = reconcileTargetsForDestTypeSwitch(targets, catalogSqlTableByResource, this._previousDestType, type);
    }
    this._previousDestType = type;

    for (const r of resources) {
      if (targets[r]) continue;
      // Seed the per-resource target once; preserve any value the user has already typed.
      const def = this.defFor(r);
      // MySQL/PostgreSQL are relational like SQL Server (def.sqlTable); Mongo has no dedicated default
      // collection name of its own, so it reuses the same table name as a sensible default collection.
      // Blob's per-resource target becomes the blob name stem/folder (MappingProfile.DestinationObject), not
      // a container — the container itself is a single wizard-level field (blobForm.container) — so it seeds
      // from the same file-name-shaped default CSV uses, minus the ".csv" extension (the blob writer already
      // appends its own real extension — a literal "patients.csv" stem would double up as "patients.csv_....ndjson").
      // def.sqlTable is hand-authored as "dbo.X" (see DEST_RESOURCE_DEFS/genericResourceDef above) — a SQL
      // Server assumption baked into the static catalog. Strip that and re-qualify per the real destType
      // (matches SqlDestinationSchemaService.SplitTableName's own per-dialect default: "dbo" for SQL Server,
      // "public" for PostgreSQL, no schema layer for MySQL) — otherwise every MySQL/PostgreSQL resource seeds
      // a "dbo."-qualified default that fails at Add to Workflow with "schema 'dbo' does not exist".
      targets[r] =
        type === 'csv'
          ? def.csvFile
          : type === 'blob'
            ? def.csvFile.replace(/\.csv$/i, '')
            : this._qualifyDefaultTable(def.sqlTable, type);
    }
    this.targetByResource.set(targets);
    this.mappingRows.update((rows) =>
      rows
        .filter((row) => resources.includes(row.resource))
        .map((row) => {
          // Only re-sync rows that were on the resource's OLD primary table — never touch rows on an
          // extra/child table, which the resource's primary-target rename doesn't affect.
          const wasOnPrimary =
            row.tableName === (oldTargets[row.resource] ?? row.tableName);
          return wasOnPrimary
            ? { ...row, tableName: targets[row.resource] ?? row.tableName }
            : row;
        }),
    );
  }

  /** Strips the static resource catalog's hardcoded "dbo." (a SQL Server assumption baked into
   *  DEST_RESOURCE_DEFS/genericResourceDef's sqlTable) and re-qualifies for the real destType via the
   *  shared qualifyTableName (field-mapping-model.ts) — the one place this logic lives. */
  private _qualifyDefaultTable(sqlTable: string, type: MappingDestType): string {
    return qualifyTableName(sqlTable.replace(/^dbo\./i, ''), type);
  }

  private _populateFromNode(node: CanvasNode): void {
    const f = node.fields ?? {};
    this.resolvedDestinationId.set(f['destinationId'] || null);
    this.resolvedSecretKeyVaultName.set(f['secretKeyVaultName'] || null);
    this.resolvedSecretName.set(f['secretName'] || null);
    // Restore the de-identification profile picker from the real DestinationConfiguration row — the canvas
    // node's own fields don't carry it (it's a destination-level attribute, not a mapping/config one), so
    // without this, re-saving an edited node would silently clear whatever profile was assigned. Applies to
    // every destination type uniformly, including the hand-rolled FHIR/Aidbox branch below.
    const destinationId = f['destinationId'];
    if (destinationId) {
      this.destinationConfigSvc.getById(destinationId).subscribe({
        next: dto => this.selectedDeIdentificationProfileId.set(dto?.deIdentificationProfileId ?? null),
        error: () => this.selectedDeIdentificationProfileId.set(null),
      });
    }
    if (this.isFhir()) {
      this.fhirForm.patchValue({
        name: f['dest_name'] || 'Aidbox Production',
        baseUrl: f['dest_baseUrl'] || '',
        project: f['dest_project'] || '',
        authType: f['dest_authType'] || 'oauth2',
        writeMode: this._fhirWriteModeFromBackend(
          f['dest_writeMode'],
          f['dest_fhirWriteMode'],
        ),
        tokenEndpoint: f['dest_tokenEndpoint'] || '',
        clientId: f['dest_clientId'] || '',
        clientSecret: '',
        username: f['dest_username'] || '',
        password: '',
        bearerToken: '',
        autoFetchMissingReferences:
          f['dest_autoFetchMissingReferences'] === 'true',
        autoFetchMaxCount: f['dest_autoFetchMaxCount']
          ? Number(f['dest_autoFetchMaxCount'])
          : 25,
      });
      this._syncFhirAuthValidators(this.fhirForm.value.authType ?? null);
      if (f['dest_fhirMapMode']) {
        this.fhirMapMode.set(
          f['dest_fhirMapMode'] === 'customize' ? 'customize' : 'passthrough',
        );
      }
      if (f['dest_fhirCustomRules']) {
        try {
          this.fhirCustomRules.set(JSON.parse(f['dest_fhirCustomRules']));
        } catch {
          /* ignore malformed */
        }
      }
    } else {
      // Queued rather than applied directly — this runs from ngOnInit, before the Step 1 form component (the
      // outlet's child) has necessarily been created; the effect in the constructor flushes it onto the form
      // the moment it exists. dest_auth's 'managed-identity' fallback (vs. patchFrom's own 'sql-auth' fallback,
      // used for a brand-new form/an existing-connection pick) only matters for a node saved before dest_auth
      // was always written, so it's folded into the queued bag here rather than needing a patchFrom parameter.
      const fields = { ...f };
      if (this.isSql() && !fields['dest_auth'])
        fields['dest_auth'] = 'managed-identity';
      this._pendingFormPatch.set({ fields, target: null });
    }
    // The mapping-restore branches below (loadMappingSummary included, each of which returns early) only
    // ever populate sqlTables() with tables this mapping already uses — a saved Mapping JSON was never
    // meant to carry the destination's FULL schema. That left "+ Add a table from your database" offering
    // nothing on reopen (everything it knew about was already used). Re-probe the live database now that
    // the connection form above is populated — must run before any of the early returns below, not after.
    this._refreshSqlTablesFromLiveSchema();
    if (f['dest_resources']) {
      this.selectedResources.set(
        f['dest_resources'].split(',').filter(Boolean),
      );
    }
    // Seeded only once selectedResources() above is populated — the rules editor's resource tabs are driven
    // by it, so an earlier default would land on a resource that isn't in the restored selection.
    if (
      this.isFhir() &&
      this.fhirMapMode() === 'customize' &&
      this.selectedResources().length > 0
    ) {
      this.activeRuleResource.set(this.selectedResources()[0]);
      this._ensureFhirFieldCatalog(this.selectedResources()[0]);
    }
    if (f['dest_targets']) {
      try {
        this.targetByResource.set(JSON.parse(f['dest_targets']));
      } catch {
        /* ignore malformed */
      }
    }
    if (f['dest_extraTables']) {
      try {
        this.extraTablesByGroup.set(JSON.parse(f['dest_extraTables']));
      } catch {
        /* ignore malformed */
      }
    }
    if (f['dest_sourcePayloadFields']) {
      try {
        this.payloadFieldsByResource.set(
          JSON.parse(f['dest_sourcePayloadFields']),
        );
      } catch {
        /* ignore malformed */
      }
    }
    // Preferred: the canonical Mapping JSON — restores tables/relations/mappings in one shot, including
    // anything dest_mappings_v2 alone can't (e.g. which extra tables are children, and of what). Falls
    // back to dest_mappings_v2/dest_mappings for nodes saved before this contract existed.
    if (f['dest_mapping_summary_v1']) {
      try {
        this.loadMappingSummary(
          JSON.parse(f['dest_mapping_summary_v1']) as MappingSummaryDocument,
        );
        // loadMappingSummary derives selectedResources solely from resources that have actual mapped
        // columns in the doc — a resource checked in Step 2 but never given a field mapping in Step 3
        // is absent from it, so it would otherwise lose its checkmark on reopen. Union dest_resources
        // back in, since that field is always the full, authoritative Step-2 selection.
        if (f['dest_resources']) {
          const fromDestResources = f['dest_resources']
            .split(',')
            .filter(Boolean);
          this.selectedResources.update((list) =>
            Array.from(new Set([...list, ...fromDestResources])),
          );
        }
        // loadMappingSummary just overwrote targetByResource with the Mapping JSON's OWN inferred primary
        // (isPrimary if the doc has it, else the older "primary = whichever table has no genuine relation"
        // guess — see resolvePrimaryTable). dest_targets (parsed into targetByResource above, before that
        // overwrite) is a second, independently-persisted record of the same fact — already treated as
        // authoritative by workflow-build-assembler.service.ts on the save/build path (see its own comment
        // there) — and reconciling against it here fixes an ALREADY-SAVED document that predates isPrimary
        // (an independent Mongo extra collection saved with no relation and no isPrimary flag, where the
        // guess can pick the wrong table), not just documents saved after this landed. A correctly-saved
        // document already agrees with dest_targets, so this is a no-op for anything not actually broken.
        if (f['dest_targets']) {
          try {
            const savedTargets = JSON.parse(f['dest_targets']) as Record<string, string>;
            const targets = { ...this.targetByResource() };
            const extras = { ...this.extraTablesByGroup() };
            for (const [resource, savedPrimary] of Object.entries(savedTargets)) {
              const currentPrimary = targets[resource];
              if (!savedPrimary || currentPrimary === savedPrimary) continue;
              const resourceExtras = extras[resource] ?? [];
              // Only reconcile when dest_targets names a table this resource's Mapping JSON actually
              // mapped something onto (as either the guessed primary or one of its extras) — never invent
              // a table the summary never mapped anything onto.
              const knownTables = new Set([currentPrimary, ...resourceExtras].filter(Boolean));
              if (!knownTables.has(savedPrimary)) continue;
              targets[resource] = savedPrimary;
              extras[resource] = [
                ...resourceExtras.filter((t) => t !== savedPrimary),
                ...(currentPrimary && currentPrimary !== savedPrimary ? [currentPrimary] : []),
              ];
            }
            this.targetByResource.set(targets);
            this.extraTablesByGroup.set(extras);
          } catch {
            /* ignore malformed */
          }
        }
        return;
      } catch {
        /* fall through to the older loaders below */
      }
    }
    if (f['dest_mappings_v2']) {
      try {
        this.mappingRows.set(JSON.parse(f['dest_mappings_v2']) as MappingRow[]);
        return;
      } catch {
        /* fall through to the legacy loader below */
      }
    }
    if (f['dest_mappings']) {
      try {
        const saved = JSON.parse(f['dest_mappings']) as LegacyMappingRow[];
        this.mappingRows.set(saved.map(migrateLegacyRow));
      } catch {
        /* ignore malformed */
      }
    }
  }

  /** Silently re-loads the live table list and replaces sqlTables() with it — unlike testConnection(),
   *  this never touches probeState()'s error path, step navigation, or provisioning; a failed reconnect
   *  just leaves the mapping-summary-restored (partial) list in place rather than blocking the editor.
   *
   *  Reopening an EXISTING/resolved destination never has a plaintext password to probe with — it's
   *  deliberately stripped before the node is persisted (see workflow-graph-mapper.service.ts's
   *  SECRET_FIELD_KEYS), so dest_password is always empty here. Using resolvedDestinationId() instead
   *  reads the schema server-side via the destination's real, already-provisioned secret reference
   *  (DestinationSchemaController's GetSchema), which needs no password from the client at all. The
   *  ad-hoc probe() (needs a real password) only applies to a brand-new, not-yet-saved connection, which
   *  never reaches this method — see _populateFromNode's only caller, editing an existing node.*/
  private _refreshSqlTablesFromLiveSchema(): void {
    // MySQL/PostgreSQL are SQL-family too (see isSql()) — DestinationSchemaController.GetSchema already
    // supports all three (SqlDestinationSchemaService.IsSupported), so this used to silently skip the
    // live-schema refresh for MySQL/PostgreSQL destinations, leaving their mapping canvas showing whatever
    // stale table/column list the last saved mapping summary happened to restore.
    if (!this.isSql()) return;

    const destinationId = this.resolvedDestinationId();
    const applyTables = (tables: DestinationTable[]) => {
      this.sqlTables.set(
        tables.map((t) => ({
          ...t,
          origin: 'probed' as const,
          columns: t.columns.map((c) => ({ ...c, origin: 'probed' as const })),
        })),
      );
      this.probeState.set('ok');
    };

    if (destinationId) {
      this.schemaSvc.getSchema(destinationId).subscribe({
        next: (res) => applyTables(res.tables),
        error: () => {
          /* keep the mapping-summary-restored list; don't block editing on a failed reload */
        },
      });
      return;
    }

    // Ad-hoc probe path for a not-yet-provisioned SQL node — needs the live form's raw values (including its
    // plaintext password), which may not exist yet this early (see _populateFromNode's queued patch above).
    // Not worth deferring further: this is a narrow edge case (editing a brand-new, never-saved SQL node),
    // and skipping just leaves the mapping-summary-restored (partial) table list in place, same fallback as
    // every other failure path here.
    const form = this.activeForm();
    if (!isSqlFamilyForm(form)) return;
    const request = form.getProbeRequest();
    if (!request.server || !request.database || !request.password) return;

    this.schemaSvc.probe(request).subscribe({
      next: (res) => {
        if (res.connected) applyTables(res.tables);
      },
      error: () => {
        /* keep the mapping-summary-restored list; don't block editing on a failed reconnect */
      },
    });
  }

  // FHIR is hand-rolled, not registry-routed (see isFhir()'s doc comment), so it needs its own getFullConfig()/
  // getMetadata()-shaped builders — every other type's equivalent now lives on its own destination-forms/
  // component (see e.g. SqlFamilyDestinationFormComponent), reached through activeFormConfig()/activeForm().
  // activeFormConfig() resolves to {} for FHIR (activeForm() is null — the registry outlet never mounts for
  // it), so _save() must reach this directly instead for its node-config bag.
  //
  // Full dest_*-keyed config, INCLUDING secret-bearing keys (dest_clientSecret/dest_password/dest_bearerToken)
  // — mirrors WizardDestinationFormApi.getFullConfig()'s contract exactly (secrets included here, stripped
  // later by workflow-graph-mapper.service.ts's SECRET_FIELD_KEYS, same as every other destination type).
  private _getFhirFullConfig(): Record<string, string> {
    const v = this.fhirForm.value;
    const config: Record<string, string> = {};
    config['dest_name'] = v.name ?? '';
    config['dest_baseUrl'] = v.baseUrl ?? '';
    // Mirrors the `selected.target` stamp the "reuse existing destination" branch sets below — without this,
    // a freshly-created FHIR destination's own node fields never carry `target`, and a graph-driven run falls
    // back to DestinationNodeExecutors.CreateDestinationConfiguration()'s raw-field reconstruction, which used
    // to leave Target null and throw. That fallback now also reads dest_baseUrl, but stamping it here directly
    // keeps the node's own config consistent with the persisted DestinationConfigurations row from the start.
    config['target'] = v.baseUrl ?? '';
    config['dest_project'] = v.project ?? '';
    config['dest_authType'] = v.authType ?? 'oauth2';
    // "Upsert by resource id (Bundle)"/"(Transaction)" are Write-mode options that are really a combination of
    // two orthogonal things: it's still upsert-by-id semantics (dest_writeMode), just delivered as one bundled/
    // transactional request instead of N individual ones (dest_fhirWriteMode — see
    // MappedFhirRepositoryDestinationWriter's own doc comment). Folded into one dropdown rather than two separate
    // fields since there's no other real second dimension to conflict with — a prior "conditional update by
    // identifier" option was removed since it was never implemented server-side.
    const writeMode = v.writeMode ?? 'upsert';
    config['dest_writeMode'] =
      writeMode === 'upsertBundle' || writeMode === 'upsertTransaction'
        ? 'upsert'
        : writeMode;
    config['dest_fhirWriteMode'] =
      writeMode === 'upsertBundle'
        ? 'bundle'
        : writeMode === 'upsertTransaction'
          ? 'transaction'
          : 'individual';
    if (v.authType === 'oauth2') {
      config['dest_tokenEndpoint'] = v.tokenEndpoint ?? '';
      config['dest_clientId'] = v.clientId ?? '';
      config['dest_clientSecret'] = v.clientSecret ?? '';
    } else if (v.authType === 'basic') {
      config['dest_username'] = v.username ?? '';
      config['dest_password'] = v.password ?? '';
    } else if (v.authType === 'bearer') {
      config['dest_bearerToken'] = v.bearerToken ?? '';
    }
    config['dest_fhirMapMode'] = this.fhirMapMode();
    if (this.fhirMapMode() === 'customize') {
      config['dest_fhirCustomRules'] = JSON.stringify(this.fhirCustomRules());
    }
    // Only ever fetches a reference the write-time existence check already confirmed missing from both the
    // batch and the destination — never a first resort. Omitted (not just "false") when off, so an existing
    // destination saved before this capability existed reads back as off, same as every other opt-in dest_* flag.
    if (v.autoFetchMissingReferences) {
      config['dest_autoFetchMissingReferences'] = 'true';
      config['dest_autoFetchMaxCount'] = String(v.autoFetchMaxCount ?? 25);
    }
    return config;
  }

  private static readonly FHIR_SECRET_FIELD_KEYS = [
    'dest_clientSecret',
    'dest_password',
    'dest_bearerToken',
  ];

  /** Non-secret fields (for connectionMetadataJson) + the already-assembled secret blob (for inlineSecret) —
   *  the getMetadata()-shaped contract provisionDestinationConnection()/next()/testFhirConnection() need. */
  private _getFhirMetadata(): {
    fields: Record<string, string>;
    secret?: string | null;
  } | null {
    if (this.fhirForm.invalid) return null;
    const full = this._getFhirFullConfig();
    const fields: Record<string, string> = {};
    for (const [key, value] of Object.entries(full)) {
      if (!DestinationWizardComponent.FHIR_SECRET_FIELD_KEYS.includes(key))
        fields[key] = value;
    }
    const secret = buildFhirSecretBlob(full);
    return { fields, secret };
  }

  // Creates (or updates, if Step 1 was already provisioned earlier this session) the real DestinationConfiguration
  // as soon as Step 1's connection details are complete — so a real destinationId exists immediately, the same way
  // sourceConnectionId now does for the Epic source wizard (see wizard.service.ts's save()), rather than only after
  // the whole workflow gets built. Skipped when reusing an existing connection (selectedExistingId already has a
  // real id) or when editing a destination whose connection came from an "existing" pick (same reason).
  //
  // `metadata` is whatever the Step 1 form's own getMetadata() just returned — its `fields` are the non-secret
  // dest_* bag (used as connectionMetadataJson) and `secret` is the already-assembled connection string/URI
  // (used as inlineSecret). This replaces per-family inline buildSqlConnectionString/buildSftpUri/
  // buildConnectionMetadata calls that used to live here — each destination-forms/ component now does that
  // assembly itself (see e.g. SqlFamilyDestinationFormComponent.getMetadata()).
  private provisionDestinationConnection(
    metadata: { fields: Record<string, string>; secret?: string | null },
    onDone: () => void,
  ): void {
    if (this.connectionMode() === 'existing') {
      onDone();
      return;
    }

    const isSql = this.isSql();
    const isMongo = this.isMongo();
    const isMedplum = this.isMedplum();
    const isFhir = this.isFhir();
    const isAzureFhir = this.isAzureFhir();
    const isBlob = this.isBlob();
    const name =
      metadata.fields['dest_name'] ||
      (isSql
        ? 'SQL Destination'
        : isMongo
          ? 'MongoDB Destination'
          : isMedplum
            ? 'Medplum Destination'
            : isFhir
              ? 'Aidbox Destination'
              : isAzureFhir
                ? 'Azure FHIR Service Destination'
                : isBlob
                  ? 'Azure Blob Destination'
                  : 'File Destination');
    const secretName = newSecretName(name);
    const deIdentificationProfileId = this.selectedDeIdentificationProfileId();
    const request: CreateDestinationConfigurationRequest = isSql
      ? {
          name,
          destinationType: this.isMySql()
            ? 'MySql'
            : this.isPostgres()
              ? 'PostgreSql'
              : 'SqlServer',
          keyVaultName: 'workflow-secrets',
          secretName,
          target: null,
          inlineSecret: metadata.secret ?? '',
          connectionMetadataJson: JSON.stringify(metadata.fields),
          deIdentificationProfileId,
        }
      : isMongo
        ? {
            name,
            destinationType: 'Mongo',
            keyVaultName: 'workflow-secrets',
            secretName,
            // null, not the primary collection — matches SQL's own `target: null` above. Mongo can now map
            // more than one resource to more than one collection (the mapping canvas's "+ Add a collection"
            // picker), each resolved per-resource via its own MappingProfile.DestinationObject
            // (workflow-build-assembler.service.ts's buildMappingForResource, already generic/not SQL-only).
            // MappedMongoDestinationWriter resolves `destination.Target ?? mappingProfile.DestinationObject`
            // — a non-null Target here would win for EVERY resource's write, collapsing every extra
            // collection back onto the primary one (confirmed: this was exactly why a second collection
            // added via the canvas was never actually created).
            target: null,
            inlineSecret: metadata.secret ?? '',
            connectionMetadataJson: JSON.stringify(metadata.fields),
            deIdentificationProfileId,
          }
        : isMedplum
          ? {
              name,
              destinationType: 'Medplum',
              keyVaultName: 'workflow-secrets',
              secretName,
              // FHIR base URL is the target; the client secret / PEM key is the whole opaque inlineSecret — the
              // Step 1 form's getMetadata() already assembled both (fields + secret), same as every other family.
              target: metadata.fields['dest_medplumBaseUrl'] || null,
              inlineSecret: metadata.secret ?? '',
              connectionMetadataJson: JSON.stringify(metadata.fields),
              deIdentificationProfileId,
            }
          : isFhir
            ? {
                name,
                destinationType: 'FhirRepository',
                keyVaultName: 'workflow-secrets',
                secretName,
                // The FHIR base URL doubles as the destination's target, mirroring how the CSV branch below uses its
                // file pattern — it's what a later "select existing" repopulates baseUrl from.
                target: metadata.fields['dest_baseUrl'] || null,
                inlineSecret: metadata.secret ?? '',
                connectionMetadataJson: JSON.stringify(metadata.fields),
                deIdentificationProfileId,
              }
            : isAzureFhir
              ? {
                  name,
                  destinationType: 'AzureFhirService',
                  keyVaultName: 'workflow-secrets',
                  secretName,
                  // Same target convention as the FHIR (Aidbox) branch above — AzureFhirServiceDestinationFormComponent's
                  // getFullConfig() also emits the FHIR service URL as dest_baseUrl.
                  target: metadata.fields['dest_baseUrl'] || null,
                  // AzureFhirServiceDestinationFormComponent.getMetadata() already folds managed identity's "no Key
                  // Vault secret" rule into metadata.secret (buildFhirSecretBlob returns '' for it) — no extra
                  // auth-mode check needed here, mirroring the Blob branch below.
                  inlineSecret: metadata.secret ?? '',
                  connectionMetadataJson: JSON.stringify(metadata.fields),
                  deIdentificationProfileId,
                }
              : isBlob
              ? {
                  name,
                  destinationType: 'BlobStorage',
                  keyVaultName: 'workflow-secrets',
                  secretName,
                  target: metadata.fields['dest_blobContainer'] || null,
                  // BlobStorageDestinationFormComponent.getMetadata() already folds Managed Identity's "no Key Vault
                  // secret" rule into metadata.secret — no extra auth-mode check needed here.
                  inlineSecret: metadata.secret ?? '',
                  connectionMetadataJson: JSON.stringify(metadata.fields),
                  deIdentificationProfileId,
                }
              : {
                  name,
                  destinationType: 'Csv',
                  keyVaultName: 'workflow-secrets',
                  secretName,
                  target: metadata.fields['dest_filePattern'] || null,
                  inlineSecret: metadata.secret ?? '',
                  connectionMetadataJson: JSON.stringify(metadata.fields),
                  deIdentificationProfileId,
                };

    const existingId = this.resolvedDestinationId();
    this.provisioningDestination.set(true);
    const obs = existingId
      ? this.destinationConfigSvc.update(existingId, request)
      : this.destinationConfigSvc.create(request);
    obs.subscribe({
      next: (dto) => {
        this.resolvedDestinationId.set(dto.id);
        this.resolvedSecretKeyVaultName.set(dto.keyVaultName);
        this.resolvedSecretName.set(dto.secretName);
        this.provisioningDestination.set(false);
        onDone();
      },
      error: (err) => {
        this.provisioningDestination.set(false);
        const msg =
          err?.error?.title ??
          err?.error?.error ??
          err?.message ??
          'Failed to create the destination connection.';
        this.toast.show(
          'Destination not created',
          typeof msg === 'string'
            ? msg
            : 'Failed to create the destination connection.',
        );
      },
    });
  }

  private _save(): void {
    // Drop any mapping row left behind pointing at a column that's since been renamed/dropped directly in
    // the database (rather than through this wizard) — without this, a save can silently persist (and a
    // later workflow run can fail on) a column that no longer exists anywhere. Applied to the actual
    // mappingRows signal, not just the outgoing payload, so the Mapping list UI stops showing the ghost row too.
    const pruned = pruneOrphanedMappingRows(
      this.mappingRows(),
      this.sqlTables(),
    );
    if (pruned.length !== this.mappingRows().length) {
      this.mappingRows.set(pruned);
    }

    const type = this.destType();
    // FHIR bypasses the registry entirely (see isFhir()'s doc comment), so activeFormConfig() resolves to {}
    // for it (activeForm() is null, no outlet mounts) — reach its own full-config builder directly instead.
    const config: Record<string, string> = {
      dest_resources: this.selectedResources().join(','),
      ...(this.isFhir() ? this._getFhirFullConfig() : this.activeFormConfig()),
    };

    // Reusing an existing DestinationConfiguration — three outcomes depending on what, if anything, the form
    // still differs from what selectExisting() patched in:
    //  - Untouched: wire the node straight to the already-saved record (id + its real secret reference/target).
    //    destinationResolved tells workflow-build-assembler.service.ts to skip this node entirely — no create,
    //    no update, so a connection another workflow also points at can never be mutated by this save.
    //  - Edited, name left alone: fork it as a new, independent connection named "<original>-1" (deduped),
    //    since the original itself already holds the unsuffixed name.
    //  - Edited, name also changed by hand: honor that name as typed (only deduped on an actual collision) rather
    //    than silently suffixing a name the user deliberately chose.
    if (this.connectionMode() === 'existing' && this.selectedExistingId()) {
      const selected = this.existingOptions().find(
        (o) => o.id === this.selectedExistingId(),
      );
      if (selected && !this.hasExistingChanged()) {
        config['destinationId'] = selected.id;
        config['secretKeyVaultName'] = selected.keyVaultName;
        config['secretName'] = selected.secretName;
        if (selected.target) config['target'] = selected.target;
        config['destinationResolved'] = 'true';
      } else if (selected) {
        const currentName = (config['dest_name'] || selected.name).trim();
        const nameWasEdited = currentName !== selected.name;
        config['dest_name'] = nameWasEdited
          ? this._resolveUniqueName(currentName, false)
          : this._resolveUniqueName(selected.name, true);
      }
    } else if (
      this.connectionMode() === 'new' &&
      this.resolvedDestinationId()
    ) {
      // Step 1 already created/updated the real DestinationConfiguration with these exact connection details
      // (see provisionDestinationConnection) — mark it resolved so workflow-build-assembler.service.ts doesn't
      // redundantly recreate the secret under a new name on every build.
      config['destinationId'] = this.resolvedDestinationId()!;
      config['secretKeyVaultName'] = this.resolvedSecretKeyVaultName() ?? '';
      config['secretName'] = this.resolvedSecretName() ?? '';
      config['destinationResolved'] = 'true';
    }

    // Persist per-resource targets + the actual field mappings (previously discarded).
    // dest_mappings keeps the legacy flat shape (one entry per row, primary/first source only) so
    // workflow-build-assembler.service.ts keeps working unmodified; dest_mappings_v2 round-trips the
    // full rich shape (joins, instance selection) so re-opening the wizard restores them exactly.
    config['dest_mappingCount'] = String(this.mappingRows().length);
    config['dest_targets'] = JSON.stringify(this.targetByResource());
    // dest_extraTables is disabled (not removed — re-enable if needed later): fully superseded by
    // dest_mapping_summary_v1, which restores extraTablesByGroup on its own via loadMappingSummary.
    // config['dest_extraTables']  = JSON.stringify(this.extraTablesByGroup());
    // dest_sourcePayloadFields is the only thing that restores payloadFieldsByResource on reopen — without
    // it, reopening a saved destination node forgets any "Load JSON payload" data and falls back to the
    // built-in generic field list, making the source tree look unloaded even though mapping rows survive.
    config['dest_sourcePayloadFields'] = JSON.stringify(
      this.payloadFieldsByResource(),
    );
    config['dest_mappings'] = JSON.stringify(
      serializeRowsFlat(
        this.mappingRows(),
        this.targetByResource(),
        this.sqlTables(),
        this.childTableRelationsByTable(),
      ),
    );
    config['dest_mappings_v2'] = JSON.stringify(this.mappingRows());
    // This wizard's own Field Mapping node's previously-saved profile id per resource (mappingProfileIds,
    // falling back to the legacy singular mappingProfileId for the primary resource) — passed through so the
    // import call below updates THOSE exact profiles rather than letting the backend search for "the" profile
    // matching (resourceType, sourceConnectionId, destinationId), a triple more than one workflow can share.
    const mappingNodeForIds = this.pipelineStore.byId(this.attachNode().id);
    const existingMappingProfileIdByResource =
      this._parseExistingMappingProfileIds(mappingNodeForIds?.fields ?? {});

    // The canonical Mapping JSON (see field-mapping-summary.model.ts) — additive alongside the two keys
    // above; this is what _populateFromNode prefers on reload, and what "Save mapping"/the export
    // preview modal show. Includes what dest_mappings_v2 alone can't: which extra tables are children
    // and of what (childTableRelationsByTable). Also what POST mapping-profiles/import sends verbatim below.
    const doc = buildMappingSummaryDocument({
      sourceVendor: this.sourceVendor().toUpperCase(),
      destType: this.nonFhirDestType(),
      destLabel: this.destLabel(),
      mappingRows: this.mappingRows(),
      sqlTables: this.sqlTables(),
      childTableRelationsByTable: this.childTableRelationsByTable(),
      availableFields: this.availableFieldsFn,
      sourceConnectionId: this.sourceConnectionId(),
      destinationId: this.selectedExistingId() ?? this.resolvedDestinationId(),
      targetByResource: this.targetByResource(),
      existingMappingProfileIdByResource,
    });
    config['dest_mapping_summary_v1'] = JSON.stringify(doc);

    const emitSaved = () => {
      this.saved.emit({
        attachNode: this.attachNode(),
        transformId:
          type === 'sql'
            ? 'dest-sqlserver'
            : type === 'mysql'
              ? 'dest-mysql'
              : type === 'postgres'
                ? 'dest-postgres'
                : type === 'mongo'
                  ? 'dest-mongo'
                  : type === 'medplum'
                    ? 'dest-medplum'
                    : type === 'fhir'
                      ? 'dest-fhir'
                      : type === 'azurefhir'
                        ? 'dest-azurefhir'
                        : type === 'blob'
                          ? 'dest-blob'
                          : 'dest-csv',
        status: 'enabled',
        config,
      });
    };

    // Import the mapping profile(s) now that both real ids exist — skipped when either is still missing
    // (e.g. no source configured yet) or there's nothing mapped, so this stays a no-op for those cases
    // exactly like before this endpoint existed. Either way "Add to Pipeline"/"Update" still completes —
    // a failed import is surfaced as a toast, not a blocker, since the node's own local save (config above)
    // never depended on it.
    if (
      doc.sourceConnectionId &&
      doc.destinationId &&
      doc.mappings.length > 0
    ) {
      this.savingMappingProfiles.set(true);
      this.mappingProfileImportSvc.import(doc).subscribe({
        next: (result) => {
          this.savingMappingProfiles.set(false);
          const failed = result.profiles.filter(
            (p) => p.warnings.length > 0 && p.mappingProfileId === EMPTY_GUID,
          );
          if (failed.length) {
            this.toast.show(
              'Mapping profile import had issues',
              failed
                .map((p) => `${p.resourceType}: ${p.warnings.join(' ')}`)
                .join(' '),
            );
          } else {
            this.toast.success(
              'Mapping profile saved',
              `${result.profiles.length} resource mapping${result.profiles.length === 1 ? '' : 's'} imported.`,
            );
          }
          // Stamp every resource's real, server-assigned mappingProfileId straight onto the Field Mapping node
          // this wizard is attached to — without this, the ids this call just returned are discarded, the next
          // save's existingMappingProfileIdByResource comes back empty, and both this endpoint and
          // /workflows/build would mint fresh, unrelated duplicate profiles instead of reusing these (and, pre-
          // fix, fall back to searching by (resourceType, sourceConnectionId, destinationId) — the exact search
          // that let one workflow's save silently overwrite another's profile). Keeps the legacy singular
          // mappingProfileId in sync too (primary resource only) for anything still reading that older field.
          const succeeded = result.profiles.filter(
            (p) => p.mappingProfileId !== EMPTY_GUID,
          );
          if (succeeded.length > 0) {
            const mappingNode = this.pipelineStore.byId(this.attachNode().id);
            if (mappingNode) {
              const mappingProfileIds = {
                ...this._parseExistingMappingProfileIds(
                  mappingNode.fields ?? {},
                ),
                ...Object.fromEntries(
                  succeeded.map((p) => [p.resourceType, p.mappingProfileId]),
                ),
              };
              const primary =
                succeeded.find(
                  (p) => p.resourceType === doc.mappings[0]?.resourceType,
                ) ?? succeeded[0];
              this.pipelineStore.updateNode(mappingNode.id, {
                fields: {
                  ...mappingNode.fields,
                  mappingProfileId: primary.mappingProfileId,
                  mappingProfileIds: JSON.stringify(mappingProfileIds),
                },
              });
            }
          }
          emitSaved();
        },
        error: (err) => {
          this.savingMappingProfiles.set(false);
          const msg =
            err?.error?.title ??
            err?.error?.error ??
            err?.message ??
            'Failed to import the mapping profile.';
          this.toast.show(
            'Mapping profile not saved',
            typeof msg === 'string'
              ? msg
              : 'Failed to import the mapping profile.',
          );
          emitSaved();
        },
      });
      return;
    }

    emitSaved();
  }
}
