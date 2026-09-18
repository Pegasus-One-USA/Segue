import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { Router } from '@angular/router';
import { LicenseService } from '../../services/license.service';
import { SettingsPageDialogService } from '../../services/settings-page-dialog.service';
import { LICENSE_UNLIMITED } from '../../models/license.model';
import { ToastService } from '../../../services/toast.service';
import { IEhrEndpointService } from '../../../ehr-endpoints/services/i-ehr-endpoint.service';
import { EhrEndpoint, EhrVendor } from '../../../ehr-endpoints/models/ehr-endpoint.model';

/// One checkable entry in the "Allowed Source Types" checklist. `SourceSystemType` (the backend enum this
/// mirrors) declares more members than this — Cerner, Allscripts, MeditechGreenfield, NewEHR, NewEHRTwo,
/// GenericFhir, Hl7v2 have no real registered connector (see FHIRBridge.Runtime.Infrastructure's
/// DependencyInjection.cs: only EpicFhirSourceClient, AthenahealthFhirSourceClient,
/// EClinicalWorksFhirSourceClient, and SampleFhirSourceClient are wired up) — so this list is deliberately
/// narrowed to the three real EHR vendor connectors actually usable today, not the full enum. Healow is
/// labeled "Healow (eClinicalWorks)" per the domain note that Healow IS eCW — there is no separate
/// SourceSystemType value for it.
interface SourceTypeOption {
  value: EhrVendor;
  label: string;
}

const SOURCE_TYPE_OPTIONS: SourceTypeOption[] = [
  { value: 'Epic', label: 'Epic' },
  { value: 'Athenahealth', label: 'Athenahealth' },
  { value: 'Healow', label: 'Healow (eClinicalWorks)' },
];

/// One checkable entry in the "Allowed Resource Types" checklist. Unlike SOURCE_TYPE_OPTIONS/
/// DESTINATION_TYPE_OPTIONS above, this is NOT narrowed down from the full backend enum — every FHIR
/// resource type in SupportedFhirResourceTypes.All is genuinely configurable today (Mapping Profiles and
/// the Destination Wizard both offer the complete set via SUPPORTED_RESOURCE_TYPES in the portal's
/// scope-constants.data.ts; only one narrower picker (FHIR_RESOURCES, a 12-entry default starting
/// selection) uses a smaller subset, and that's a UI convenience, not a real restriction). So this list
/// mirrors SupportedFhirResourceTypes.All in full.
const RESOURCE_TYPE_OPTIONS: readonly string[] = [
  'Patient', 'Practitioner', 'Observation', 'Condition', 'MedicationRequest', 'MedicationAdministration',
  'AllergyIntolerance', 'Encounter', 'DiagnosticReport', 'Procedure', 'ServiceRequest', 'Immunization',
  'Appointment', 'Binary', 'CarePlan', 'CareTeam', 'Communication', 'CommunicationRequest', 'Device',
  'DocumentReference', 'FamilyMemberHistory', 'Goal', 'ImagingStudy', 'Location', 'Medication',
  'MedicationDispense', 'MedicationStatement', 'Organization', 'PractitionerRole', 'Provenance',
  'Questionnaire', 'QuestionnaireResponse', 'RelatedPerson', 'Schedule', 'Slot', 'Specimen', 'Task',
  'Account', 'AdverseEvent', 'BodyStructure', 'Claim', 'Consent', 'Contract', 'Coverage', 'DeviceRequest',
  'DeviceUseStatement', 'Endpoint', 'EpisodeOfCare', 'ExplanationOfBenefit', 'Flag', 'Group',
  'ImmunizationRecommendation', 'List', 'Media', 'NutritionOrder', 'RequestGroup', 'ResearchStudy',
  'ResearchSubject', 'Substance',
];

/// One checkable entry in the "Allowed Destination Types" checklist. `DestinationType` (the backend enum
/// this mirrors) declares 26 members, but most are Phase 2+/stub — this list is deliberately narrowed to
/// exactly the destination types actually offered as a real, working create-flow card today (see
/// destination-connection-dialog.component.ts's CREATE_TYPES — kept identical to it on purpose, since a
/// license shouldn't be able to allow-list a destination type customers can't yet configure).
const DESTINATION_TYPE_OPTIONS: readonly string[] = [
  'SqlServer', 'PostgreSql', 'MySql', 'Mongo', 'BlobStorage', 'Csv', 'FhirRepository', 'Medplum',
  'AzureFhirService', 'DataLakeWebhook',
];

// ⚠️⚠️⚠️ TEMPORARY / DEV-ONLY PAGE — DO NOT SHIP, DO NOT USE FOR ANY REAL CUSTOMER ⚠️⚠️⚠️
//
// This page mints signed license tokens by calling the throwaway `POST /api/v1/dev/license-mint`
// endpoint (DevLicenseMintingController), which signs with a dev-only private key and 404s itself on
// any non-Development host — see that controller's remarks for the full rationale. This page exists
// only as a stand-in until license minting moves to its own separate internal tool/portal, per the
// plan discussed when this was added. It is linked from the License settings page
// (license-settings.component.html's "Dev: Mint a test license →" button) and is NOT in the main
// sidebar navigation.
//
// DELETE THIS WHOLE FOLDER — license-dev-mint.component.ts/.html/.scss — along with the
// 'license/mint-dev' route in settings.routes.ts, LicenseService.mintDev(), the
// DevLicenseMintRequest/DevLicenseMintResponse models, LICENSE_ENDPOINTS.devMint, and the link on the
// License settings page, once minting moves elsewhere.
@Component({
  selector:    'app-license-dev-mint',
  standalone:  true,
  imports:     [ReactiveFormsModule],
  templateUrl: './license-dev-mint.component.html',
  styleUrl:    './license-dev-mint.component.scss',
})
export class LicenseDevMintComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly licenseSvc = inject(LicenseService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly settingsPageDialog = inject(SettingsPageDialogService);
  private readonly ehrEndpointSvc = inject(IEhrEndpointService);

  protected readonly minting = signal(false);
  protected readonly mintError = signal<string | null>(null);
  protected readonly mintedToken = signal<string | null>(null);

  protected readonly applying = signal(false);

  protected readonly form = this.fb.nonNullable.group({
    customerId:                  ['', Validators.required],
    customerName:                [''],
    edition:                     [''],
    expiresUtc:                  ['', Validators.required],
    maxUsers:                    [''],
    maxWorkflows:                [''],
    maxSourceConnections:        [''],
    maxProcessedRecordsPerMonth: [''],
    maxSuccessfulWorkflowExecutionsPerMonth: [''],
    features:                    [''],
    activationWindowMinutes:     [''],
  });

  // ── Allowed Source Types checklist ──────────────────────────────────────────────────────────
  protected readonly sourceTypeOptions = SOURCE_TYPE_OPTIONS;
  protected readonly selectedSourceTypes = signal<ReadonlySet<string>>(new Set());

  protected toggleSourceType(value: string): void {
    this.selectedSourceTypes.update((current) => {
      const next = new Set(current);
      if (next.has(value)) { next.delete(value); } else { next.add(value); }
      return next;
    });
  }

  // ── Allowed Resource Types checklist ────────────────────────────────────────────────────────
  protected readonly resourceTypeOptions = RESOURCE_TYPE_OPTIONS;
  protected readonly selectedResourceTypes = signal<ReadonlySet<string>>(new Set());

  protected toggleResourceType(value: string): void {
    this.selectedResourceTypes.update((current) => {
      const next = new Set(current);
      if (next.has(value)) { next.delete(value); } else { next.add(value); }
      return next;
    });
  }

  // ── Allowed Destination Types checklist ─────────────────────────────────────────────────────
  protected readonly destinationTypeOptions = DESTINATION_TYPE_OPTIONS;
  protected readonly selectedDestinationTypes = signal<ReadonlySet<string>>(new Set());

  protected toggleDestinationType(value: string): void {
    this.selectedDestinationTypes.update((current) => {
      const next = new Set(current);
      if (next.has(value)) { next.delete(value); } else { next.add(value); }
      return next;
    });
  }

  // ── Allowed Hospitals picker, backed by the existing EHR Endpoint directory ─────────────────
  protected readonly hospitalsLoading = signal(true);
  protected readonly hospitalsError = signal<string | null>(null);
  protected readonly hospitals = signal<EhrEndpoint[]>([]);
  protected readonly hospitalSearch = signal('');
  protected readonly selectedHospitalIds = signal<ReadonlySet<string>>(new Set());

  protected readonly filteredHospitals = computed(() => {
    const term = this.hospitalSearch().trim().toLowerCase();
    const all = this.hospitals();
    if (!term) return all;
    return all.filter(
      (h) =>
        h.name.toLowerCase().includes(term) ||
        h.vendor.toLowerCase().includes(term) ||
        h.fhirBaseUrl.toLowerCase().includes(term)
    );
  });

  ngOnInit(): void {
    this.hospitalsLoading.set(true);
    this.ehrEndpointSvc.getAll().subscribe({
      next: (endpoints) => {
        this.hospitals.set(endpoints);
        this.hospitalsLoading.set(false);
      },
      error: () => {
        this.hospitalsLoading.set(false);
        this.hospitalsError.set('Failed to load the EHR endpoint directory.');
      },
    });
  }

  protected toggleHospital(id: string): void {
    this.selectedHospitalIds.update((current) => {
      const next = new Set(current);
      if (next.has(id)) { next.delete(id); } else { next.add(id); }
      return next;
    });
  }

  protected generate(): void {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }

    this.mintError.set(null);
    this.mintedToken.set(null);
    this.minting.set(true);

    const raw = this.form.getRawValue();
    const selectedHospitalIds = this.selectedHospitalIds();
    this.licenseSvc.mintDev({
      customerId:           raw.customerId.trim(),
      customerName:         raw.customerName.trim() || null,
      edition:              raw.edition.trim() || null,
      // Native <input type="date"> yields "yyyy-MM-dd"; normalize to end-of-day UTC so the token is
      // valid through the entire selected expiry day regardless of the admin's local timezone.
      expiresUtc:           `${raw.expiresUtc}T23:59:59.000Z`,
      maxUsers:             parseIntOrUnlimited(raw.maxUsers),
      maxWorkflows:         parseIntOrUnlimited(raw.maxWorkflows),
      maxSourceConnections: parseIntOrUnlimited(raw.maxSourceConnections),
      maxProcessedRecordsPerMonth: parseIntOrUnlimited(raw.maxProcessedRecordsPerMonth),
      maxSuccessfulWorkflowExecutionsPerMonth: parseIntOrUnlimited(raw.maxSuccessfulWorkflowExecutionsPerMonth),
      features:             parseFeatures(raw.features),
      allowedSourceTypes:   Array.from(this.selectedSourceTypes()),
      allowedHospitals:     this.hospitals()
        .filter((h) => selectedHospitalIds.has(h.id))
        .map((h) => ({ vendor: h.vendor, baseUrl: h.fhirBaseUrl, displayName: h.name })),
      allowedResourceTypes:    Array.from(this.selectedResourceTypes()),
      allowedDestinationTypes: Array.from(this.selectedDestinationTypes()),
      activationWindowMinutes: parseNullableInt(raw.activationWindowMinutes),
    }).subscribe({
      next: (res) => {
        this.minting.set(false);
        this.mintedToken.set(res.token);
        this.toast.success('Test license minted', 'Copy it below, or apply it directly.');
      },
      error: (err: HttpErrorResponse) => {
        this.minting.set(false);
        this.mintError.set(
          err.status === 404
            ? 'This dev-only endpoint is only reachable when the API is running in the Development environment.'
            : (err.error?.error_description ?? 'Failed to mint a test license.')
        );
      },
    });
  }

  protected copyToken(): void {
    const token = this.mintedToken();
    if (!token) return;
    navigator.clipboard?.writeText(token).then(
      () => this.toast.success('Token copied to clipboard'),
      () => this.toast.error('Could not copy. Select and copy manually.'),
    );
  }

  protected applyNow(): void {
    const token = this.mintedToken();
    if (!token) return;

    this.applying.set(true);
    this.licenseSvc.apply({ token }).subscribe({
      next: () => {
        this.applying.set(false);
        this.toast.success('License activated', 'The freshly minted test license is now active.');
        // '/settings/license' no longer exists (License is a dialog off System Settings > General now),
        // so land on the page that hosts it and open it, rather than navigating to a dead route.
        void this.router.navigate(['/settings/system-settings/general'])
          .then(() => this.settingsPageDialog.open('license'));
      },
      error: (err: HttpErrorResponse) => {
        this.applying.set(false);
        this.toast.error(err.error?.error_description ?? 'Failed to apply the minted license.');
      },
    });
  }
}

/** Blank input, or an explicit "-1", both mean unlimited (LICENSE_UNLIMITED) for that dimension. */
function parseIntOrUnlimited(value: string): number {
  const trimmed = value.trim();
  if (!trimmed) return LICENSE_UNLIMITED;
  const parsed = Number.parseInt(trimmed, 10);
  return Number.isNaN(parsed) ? LICENSE_UNLIMITED : parsed;
}

/** Blank input means no activation deadline (`null`) — unlike `parseIntOrUnlimited`, there's no "-1 means
 *  unlimited" convention here, since an unenforced window and an infinite one are the same thing. */
function parseNullableInt(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  const parsed = Number.parseInt(trimmed, 10);
  return Number.isNaN(parsed) ? null : parsed;
}

function parseFeatures(value: string): string[] {
  return value
    .split(',')
    .map((f) => f.trim())
    .filter((f) => f.length > 0);
}
