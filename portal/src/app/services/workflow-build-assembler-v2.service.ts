import { Injectable, inject } from '@angular/core';
import { PipelineStoreV2 } from './pipeline-v2.store';
import { WorkflowGraphMapperServiceV2 } from './workflow-graph-mapper-v2.service';
import { OAUTH_DEFAULT_URLS } from '../core/api-endpoints';
import { vendorScopeProfile } from '../data/vendor-scope-catalog.data';
import {
  CreateDestinationConfigurationRequest,
  CreateSourceConnectionRequest,
  DestinationBuildSpec,
  MappingBuildSpec,
  MappingFieldRequest,
  ParentReferenceSpec,
  SourceAuthenticationRequest,
  SourceBuildSpec,
  SourceRetrievalConfigurationRequest,
  WorkflowBuildRequest,
  WorkflowNodeRequest,
  WorkflowTriggerRequest,
} from './workflow-api.service';

interface DestMappingRow {
  resource: string;
  field: string;
  path: string; // FHIR element path captured by the wizard (e.g. "Patient.name.family")
  target: string; // destination table / file (e.g. "dbo.Patient")
  column: string; // destination column
  // Array-aware metadata stamped by the wizard from the backend FHIR catalog. When present these are
  // authoritative; when absent (offline/degraded) we fall back to the naive path conversion below.
  jsonPath?: string; // e.g. "$.name[*].given[*]"
  valueType?: string; // String | Integer | Decimal | Boolean | Date | DateTime | Json
  arrays?: string[]; // array-ancestor fhir paths
  isUpsertKey?: boolean; // wizard-forced true on the resource's mandatory id row, false elsewhere
  isRequiredParentRef?: boolean; // wizard-forced true on a locked "child of" reference-field row
  parentResourceType?: string;   // which parent (of possibly several) this locked row satisfies
  // Advanced MappingFieldDto members the wizard has no UI to author (docs/backend/14-mapping-profile-master-screen-plan.md
  // §3.2) but must still round-trip losslessly when present — e.g. a row loaded from a profile the Mapping
  // Profiles master screen authored. undefined for every wizard-authored row, in which case the computed
  // defaults below (isRequired/arrayPolicy/etc.) apply exactly as before.
  isRequired?: boolean;
  defaultValue?: string;
  format?: string;
  normalizationType?: string;
  terminologySystemJsonPath?: string;
  terminologyCodeJsonPath?: string;
  arrayPolicy?: string;
  cardinality?: string;
  correlationCodeJsonPath?: string;
  correlationCodeValue?: string;
  isEnabled?: boolean;
  // True when this row's field-mapping metadata (JsonPath/arrayPolicy/etc.) was derived by naive path
  // conversion rather than the backend FHIR catalog's own authoritative shape — see field-mapping-model.ts.
  approximated?: boolean;
  // Set by field-mapping-model.ts's serializeRowsFlat only when `target` is a genuine child table of this
  // resource's own primary table (e.g. dbo.PatientName, child of dbo.Patient) — lets buildMappingForResource
  // route this one field to its own table via MappingFieldRequest.destinationObject rather than the
  // resource's single baseDestinationObject, exactly mirroring MappingImportService.BuildFieldAsync on the
  // backend for the mapping-profiles/import path.
  parentTable?: string;
  parentKeyColumn?: string;
  foreignKeyColumn?: string;
  // Set by field-mapping-list.component.ts's "which resource does this reference?" picker (round-tripped
  // through field-mapping-model.ts's serializeRowsFlat) — resolved in buildMappingForResource into the
  // referenced resource's own table/id column, since the raw FHIR reference string ("Patient/xyz") this field
  // is sourced from can never be written as-is into what's normally a NOT NULL FK column.
  referencesResource?: string;
}

/** Fresh secret name for a wizard-typed client secret — same convention as WizardServiceV2's newClientSecretName
 *  (duplicated, not imported: that one lives in a service built for entity-mode save, this one for canvas build). */
function newInlineSecretName(connectionName: string): string {
  const slug = connectionName.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 24) || 'src';
  const suffix = Math.random().toString(36).slice(2, 8);
  return `src-${slug}-${suffix}`;
}

/** The three client-authentication mechanisms the shared EHR-vendor source form offers, as written into a canvas
 *  node's field bag under 'Auth method' (see EhrVendorSourceFormComponent's authMethod control). 'public' is
 *  PKCE — no client credential at all — and is only offered for the interactive audiences. */
type WizardAuthMethod = 'public' | 'secret' | 'jwt';

/** What the caller must tell {@link buildAuthentication} about the vendor branch it is building for, beyond the
 *  field bag itself. */
interface AuthenticationBuildOptions {
  /** Resolved ApplicationType ('Backend' | 'Standalone' | 'EhrLaunch' | 'Patient'). */
  applicationType: string;
  /** Already-resolved OAuth scope list — each vendor derives this differently (destination-mapped resource types
   *  for athenahealth/eCW, the wizard's own 'Scopes' field for Epic), so it stays the caller's job. */
  scopes: string[];
  /** Slug used when minting a fresh vault secret name for a newly typed client secret (e.g. 'athena'). */
  secretSlug: string;
  /** Epic's Backend audience is required by ConfigurationService.ValidateEpicSourceConnection to be
   *  SmartBackendServices regardless of what the form's Auth Method dropdown says — so that one branch pins the
   *  value rather than deriving it. Every other vendor derives it from the selected auth method. */
  forceBackendAuthenticationType?: string;
  /** athenahealth only — the bare practice id sent as ah-practice on every request. */
  practiceId?: string | null;
  /** Epic only — scopes actually granted on the last successful Discover token exchange. */
  discoveredScopes?: string[] | null;
}

/**
 * Builds the `authentication` block of a {@link CreateSourceConnectionRequest} from a canvas node's field bag.
 *
 * This mirrors WizardServiceV2.save()'s own authentication block (the Settings → Source Connections "master"
 * path) field for field, so a connection created on the fly from the workflow canvas persists exactly what the
 * same wizard inputs would have persisted from Settings. It exists because that mapping was previously written
 * out once per vendor branch inside buildSource() and had drifted three different ways:
 *
 *  - athenahealth ignored 'Auth method' entirely and never emitted keyId/privateKey* at all, so a Backend System
 *    connection registered for private_key_jwt persisted with no key material. At run time
 *    BackendServicesApplicationStrategy.UsesJwtAssertion() tests PrivateKeyPem, found none, routed to the
 *    client-secret provider — which had no secret either — and the run failed. (AuthenticationType itself has no
 *    runtime dispatch role; the missing key reference is what actually broke it.)
 *  - Epic emitted keyId/privateKey* ungated, so an interactive (PKCE) connection persisted stale key material the
 *    master path would have nulled, and emitted no client-secret fields at all.
 *  - eClinicalWorks alone read 'Auth method' and forked correctly — the reference this helper generalizes.
 *
 * Deriving every credential field from the one selected auth method (rather than from the vendor or the
 * application type) is what keeps the two creation paths in agreement, and means a new vendor branch gets the
 * correct behaviour by calling this rather than by copying a neighbouring block.
 */
function buildAuthentication(
  fields: Record<string, string>,
  options: AuthenticationBuildOptions,
): SourceAuthenticationRequest {
  const isBackend = options.applicationType === 'Backend';
  // Backend System has no public/PKCE option (no authorization code to protect), so it defaults to a client
  // secret; the interactive audiences default to public. Matches the form's own defaultAuthMethodFor().
  const authMethod = (fields['Auth method'] || (isBackend ? 'secret' : 'public')) as WizardAuthMethod;
  const typedSecret = (fields['Client Secret'] ?? '').trim() || null;

  // Resolved FIRST, because every credential field below gates on THIS rather than on the raw auth method.
  // The two can legitimately disagree: Epic's Backend audience pins SmartBackendServices (see
  // forceBackendAuthenticationType) while 'Auth method' may still arrive as 'secret' — the form writes
  // `v.authMethod ?? 'secret'`, a vendor-agnostic fallback that fires whenever discovery didn't advertise
  // private_key_jwt. Gating the signing key on the raw method there would persist SmartBackendServices with no
  // key material AND no client secret — precisely the unrunnable combination this helper exists to prevent:
  // BackendServicesApplicationStrategy.UsesJwtAssertion() tests PrivateKeyPem, finds none, routes to the
  // client-secret provider, and that has nothing either. So the invariant is: key material follows the RESOLVED
  // authentication type, and a client secret is only ever carried by a type that actually sends one.
  const authenticationType =
    isBackend && options.forceBackendAuthenticationType
      ? options.forceBackendAuthenticationType
      : authMethod === 'jwt'
        ? 'SmartBackendServices'
        : authMethod === 'secret'
          ? 'OAuthClientCredentials'
          : 'None';
  const signsJwtAssertion = authenticationType === 'SmartBackendServices';
  const usesClientSecret = authenticationType === 'OAuthClientCredentials';

  // A non-interactive (client_credentials) app never performs a browser redirect, so it has no authorize
  // endpoint to store — master gates this on the audience's own showRedirect flag, not on whether the wizard
  // happened to discover a URL.
  const authorizationEndpoint = isBackend ? null : fields['Authorize endpoint'] || null;

  return {
    // Derived from the selected auth method, exactly as the master path's
    // AUTH_METHOD_TO_AUTHENTICATION_TYPE lookup does — public (PKCE) stores no client credential, so 'None'
    // (a real AuthenticationType member, value 0).
    authenticationType,
    clientId: fields['Client ID'] || fields['Active client ID'] || null,
    tokenEndpoint: fields['Token endpoint'] || null,
    authorizationEndpoint,
    scopes: options.scopes,
    // Client Secret auth only. A freshly typed secret is provisioned via inlineClientSecret under a brand-new
    // vault reference; a blank box sends nulls, which ConfigurationService.PreserveSecretsIfBlank reads as
    // "leave whatever is already stored untouched" rather than as "clear it".
    clientSecretKeyVaultName: usesClientSecret && typedSecret ? 'workflow-secrets' : null,
    clientSecretName:
      usesClientSecret && typedSecret
        ? newInlineSecretName(fields['__name'] || options.secretSlug)
        : null,
    inlineClientSecret: usesClientSecret ? typedSecret : null,
    // private_key_jwt (SMART Backend Services) only — the signing key is referenced by (Key Vault Name, Secret
    // Name) and identified by kid. Gated on the RESOLVED type so an interactive connection can't persist stale
    // key material, and so an audience pinned to SmartBackendServices always carries the key it must sign with.
    keyId: signsJwtAssertion ? fields['JWT kid'] || null : null,
    privateKeyKeyVaultName: signsJwtAssertion ? fields['Key vault reference'] || null : null,
    privateKeySecretName: signsJwtAssertion ? fields['Secret Name'] || null : null,
    // Informational only (FHIRBridge never fetches it), but eCW requires this URL's host to be allow-listed on
    // its own servers, so persisting what was really registered is what makes a later bare invalid_client
    // diagnosable. Previously never sent from this path at all — and since PreserveSecretsIfBlank guards only
    // ClientSecret/PrivateKey, every workflow rebuild silently nulled it on an existing row.
    jwksUrl: signsJwtAssertion ? fields['JWKS URL'] || null : null,
    // Where OAuth2ClientCredentialsTokenProvider places the client id/secret — only meaningful for a type that
    // actually sends one. Null (a nullable column the backend reads as "post") for every other type.
    authPlacement: usesClientSecret
      ? (fields['Auth placement'] as 'post' | 'basic') || 'post'
      : null,
    practiceId: options.practiceId ?? null,
    discoveredScopes: options.discoveredScopes ?? null,
  };
}

/**
 * Translates the current builder canvas + wizard fields into a {@link WorkflowBuildRequest} for the Option B
 * create-on-save endpoint (`POST /workflows/build`). Reuses {@link WorkflowGraphMapperServiceV2.toRequest} for the graph
 * (nodes/edges/synthetic-mapping) exactly as plain save does, then derives the create-specs that turn the wizard's
 * human-labelled fields into the backend DTO shapes (assembled connection secret, MappingField rows, source auth).
 *
 * Scope / known approximations (see workflow-builder-optionB-handoff.md):
 *  - One mapping profile per destination (the graph model is one mapping node → one destination): if the destination
 *    wizard selected multiple resources, only the PRIMARY (first) resource is wired; `unmappedResources` reports the rest.
 *  - Wizard field paths are FHIR element paths ("Patient.name.family"); the executor wants JSONPath. We do a best-effort
 *    conversion (strip the resource prefix, prefix "$."). Simple scalars like id map cleanly; deep arrays may not populate.
 *  - Epic source auth is mapped best-effort from the wizard; trusted issuers / private-key secret are not fully captured.
 */
@Injectable({ providedIn: 'root' })
export class WorkflowBuildAssemblerServiceV2 {
  private readonly store = inject(PipelineStoreV2);
  private readonly mapper = inject(WorkflowGraphMapperServiceV2);

  /** Resources selected on a destination but NOT wired into the build (surfaced to the user as a caveat). */
  readonly lastUnmappedResources: string[] = [];

  assemble(
    name: string,
    trigger?: WorkflowTriggerRequest | null,
    description?: string | null,
    /** The workflow's own id, so the mapper can stamp it onto every rule-resolving node. Previously hardcoded
     *  to null here, which meant a workflow saved through THIS path (the create-on-save build endpoint — the
     *  normal Save) persisted its chain nodes with no resourcePipelineRouteId. Its executors then could not
     *  tell which workflow they were running for, so every workflow-scoped transformation rule missed and the
     *  resource was written untransformed, silently, with the run still reporting success — that is how a raw
     *  birthDate reached an int column with a DateMathAge rule configured against it. The server now stamps
     *  this too (WorkflowEndpoints.StampWorkflowId), so the two are belt and braces. */
    workflowId?: string | null,
  ): WorkflowBuildRequest {
    this.lastUnmappedResources.length = 0;

    const graph = this.mapper.toRequest(name, trigger, workflowId ?? null, description);
    const nodesById = new Map(graph.nodes.map((node) => [node.id, node]));

    const sourceNodeIds = new Set(
      graph.nodes
        .filter((node) => this.isSourceNode(node))
        .map((node) => node.id),
    );

    // Pre-computed, per source node, the union of resource types its destinations actually map — used to derive
    // athenahealth's retrieval resource types (and therefore the OAuth scopes it requests) from what's genuinely
    // consumed downstream, instead of a separately-configured source-side picker that could silently drift out of
    // sync with it (the real cause of repeated "Invalid Scope" failures against the live sandbox). A cheap
    // pre-pass over dest_mappings — far cheaper than the full buildMappings() schema-diff work done in the real
    // destinations loop below, and this only needs the bare resource name per row. Also unions in dest_resources
    // (the Step 2 "Data groups" selection) directly: a FHIR-passthrough destination (Aidbox/Medplum) never
    // populates dest_mappings at all — there's no field-by-field mapping table for a whole-resource passthrough
    // write — so relying on dest_mappings alone left athenahealth's retrieval resourceTypes permanently empty for
    // that combination, failing save with "At least one resource type is required for Search (REST) retrieval"
    // even after the user picked resources in Step 2.
    const destinationResourceTypesBySourceNodeId = new Map<string, Set<string>>();
    for (const destNode of graph.nodes.filter((node) => this.isDestinationNode(node))) {
      const destFields = this.fieldsFor(destNode.id, nodesById);
      const mappingNodeId = this.mappingNodeFeeding(destNode.id, graph);
      const sourceNodeId = this.sourceFeeding(mappingNodeId ?? destNode.id, graph, sourceNodeIds);
      if (!sourceNodeId) continue;
      const mappedResources = this.parseMappingRows(destFields['dest_mappings']).map((row) => row.resource);
      const selectedResources = (destFields['dest_resources'] ?? '').split(',').map((r) => r.trim()).filter(Boolean);
      const resources = new Set([...mappedResources, ...selectedResources]);
      const set = destinationResourceTypesBySourceNodeId.get(sourceNodeId) ?? new Set<string>();
      resources.forEach((r) => set.add(r));
      destinationResourceTypesBySourceNodeId.set(sourceNodeId, set);
    }

    const sources: SourceBuildSpec[] = [];
    for (const id of sourceNodeIds) {
      const fields = this.fieldsFor(id, nodesById);
      // Existing-source pick left untouched (see EhrVendorSourceFormComponent.save()'s resolvedSourceConnectionId) —
      // skip entirely, no create/update. Mirrors destinationResolved below: a connection another workflow also
      // points at can't be mutated by this save, and the backend resolves sourceConnectionId straight off this
      // node's own config for the Mappings step regardless.
      if (fields['sourceConnectionResolved'] === 'true') continue;
      sources.push({
        nodeId: id,
        source: this.buildSource(fields, [...(destinationResourceTypesBySourceNodeId.get(id) ?? [])]),
        existingId: fields['sourceConnectionId'] || null,
      });
    }

    const destinations: DestinationBuildSpec[] = [];
    const mappings: MappingBuildSpec[] = [];

    for (const destNode of graph.nodes.filter((node) =>
      this.isDestinationNode(node),
    )) {
      const destFields = this.fieldsFor(destNode.id, nodesById);

      // destinationResolved: the wizard selected an existing connection and left it untouched — its destinationId/
      // secretKeyVaultName/secretName/target are already final on the node's own config (see
      // DestinationWizardComponent._save()), so this destination is skipped here entirely. No create, no update —
      // a connection another workflow also points at can't be mutated by this save.
      if (destFields['destinationResolved'] !== 'true') {
        destinations.push({
          nodeId: destNode.id,
          destination: this.buildDestination(destFields, destNode),
          existingId: destFields['destinationId'] || null,
        });
      }

      const mappingNodeId = this.mappingNodeFeeding(destNode.id, graph);
      const sourceNodeId = this.sourceFeeding(
        mappingNodeId ?? destNode.id,
        graph,
        sourceNodeIds,
      );
      if (!mappingNodeId || !sourceNodeId) continue;

      const mappingFields = this.fieldsFor(mappingNodeId, nodesById);
      mappings.push(
        ...this.buildMappings(
          mappingNodeId,
          sourceNodeId,
          destNode.id,
          destFields,
          mappingFields,
        ),
      );
    }

    return { ...graph, sources, destinations, mappings: this.stampInlineMappings(graph, mappings) };
  }

  /**
   * Writes each mapping node's fields into its OWN configuration, alongside the ids (plan §3.3 / phase 7).
   *
   * Until now a mapping node stored only `mappingProfileIds`, so every read had to fetch the profile by id and
   * rebuild the field list from it — which is what let the executor and the wizard disagree about what a node
   * maps, and what made a run depend on a master record that could have been edited since.
   * MappingNodeExecutor now prefers this inline block (phase 5), so what runs is what the node says.
   *
   * The ids are deliberately KEPT for now: dual-read means nothing breaks if a reader hasn't been migrated yet,
   * and the profiles are still the authoring surface the wizard loads from. Dropping them is phase 9.
   */
  private stampInlineMappings(
    graph: { nodes: WorkflowNodeRequest[] },
    mappings: MappingBuildSpec[],
  ): MappingBuildSpec[] {
    if (mappings.length === 0) return mappings;

    const byNode = new Map<string, MappingBuildSpec[]>();
    for (const spec of mappings) {
      const existing = byNode.get(spec.nodeId);
      if (existing) existing.push(spec);
      else byNode.set(spec.nodeId, [spec]);
    }

    for (const node of graph.nodes) {
      const specs = byNode.get(node.id);
      if (!specs?.length) continue;

      const inline: Record<string, { destinationObject: string; fields: MappingFieldRequest[] }> = {};
      for (const spec of specs) {
        inline[spec.resourceType] = {
          destinationObject: spec.destinationObject,
          fields: spec.fields,
        };
      }

      let config: Record<string, unknown>;
      try {
        config = JSON.parse(node.configurationJson || '{}') as Record<string, unknown>;
      } catch {
        continue;
      }

      node.configurationJson = JSON.stringify({ ...config, mappings: inline });
    }

    return mappings;
  }

  // ── source ────────────────────────────────────────────────────────────────
  private buildSource(
    fields: Record<string, string>,
    destinationResourceTypes: string[] = [],
  ): CreateSourceConnectionRequest {
    const connector = fields['Connector'] ?? fields['__name'] ?? '';
    const isSample =
      /sample/i.test(connector) || /sample/i.test(fields['__name'] ?? '');

    if (isSample) {
      return {
        name: fields['__name'] || 'Sample Source',
        sourceSystemType: 'Sample',
        baseUrl: fields['FHIR base URL'] || 'https://sample.local/fhir',
        authentication: { authenticationType: 'None', scopes: [] },
        applicationType: null,
        interactive: null,
      };
    }

    // Generic FHIR (GenericFhirSourceFormComponent) — a bare, unauthenticated conformant FHIR R4 server. No
    // OAuth/interactive concepts apply, so applicationType/interactive stay null (same as Sample). Retrieval
    // reuses the exact same buildRetrieval() Epic's Backend-System retrieval section feeds — search-rest's
    // _since/_lastUpdated incremental cursor, _count, _sort, _include/_revinclude, and bulk $export's
    // scope/group/patient/output-format are all vendor-agnostic on the backend, not Epic-specific.
    if (/generic.?fhir/i.test(connector)) {
      return {
        name: fields['__name'] || 'Generic FHIR Source',
        sourceSystemType: 'GenericFhir',
        baseUrl: fields['FHIR base URL'] || '',
        authentication: { authenticationType: 'None', scopes: [] },
        applicationType: null,
        interactive: null,
        retrieval: this.buildRetrieval(fields),
      };
    }

    // athenahealth — same shared-form field bag as Epic, and every request needs the Practice ID field's
    // ah-practice scoping. Its Backend System registrations exist BOTH as client-secret (plain client_credentials)
    // and as private_key_jwt apps, and the form offers both, so the credential fields are derived from the selected
    // Auth Method by buildAuthentication() rather than assumed from the vendor. Canvas mode has no prior connection
    // to preserve an existing secret reference from (this always builds a fresh CreateSourceConnectionRequest), so a
    // blank secret sends nulls, which ConfigurationService.PreserveSecretsIfBlank keeps rather than clears.
    if (/athenahealth/i.test(connector)) {
      const athenaAppType = this.applicationTypeFor(fields);
      // Resource types (and therefore OAuth scopes) come from what the destination actually maps — see
      // destinationResourceTypesBySourceNodeId in assemble() — not the source's own Retrieval Configuration
      // picker, which is now hidden for athenahealth in the form (ehr-vendor-source-form.component.ts's
      // visibleRetrievalFields). Falls back to whatever fields['Scopes']/['Retrieval resource type'] already
      // held when no destination is wired up yet (e.g. the very first save of a bare source node), so this
      // never regresses to an empty/invalid request. The backend's own SourceConnectionRuntimeResolver
      // regenerates the actual OAuth scope string fresh from Retrieval.ResourceTypes on every run regardless —
      // this is just what gets initially persisted/validated at build time.
      const athenaResourceTypes = destinationResourceTypes.length
        ? destinationResourceTypes
        : (fields['Retrieval resource type'] ?? '').split(',').map((s) => s.trim()).filter(Boolean);
      const athenaScopes = athenaResourceTypes.length
        ? athenaResourceTypes.map((rt) => `system/${rt}.read`)
        : (fields['Scopes'] ?? '').split(/[\s,]+/).filter(Boolean);
      const athenaBaseRetrieval = (athenaAppType === 'Backend' || athenaAppType === 'Standalone') ? this.buildRetrieval(fields) : null;
      const athenaRetrieval = athenaBaseRetrieval
        ? { ...athenaBaseRetrieval, resourceTypes: athenaResourceTypes.length ? athenaResourceTypes : athenaBaseRetrieval.resourceTypes }
        : null;
      return {
        name: fields['__name'] || 'Athenahealth',
        sourceSystemType: 'Athenahealth',
        baseUrl: fields['FHIR base URL'] || '',
        // Backend System dispatches on the credential the connection actually carries, not on the vendor:
        // athenahealth registrations exist both as client-secret and as private_key_jwt apps, and the form's Auth
        // Method dropdown offers both. buildAuthentication() derives every credential field from that choice — the
        // key material this branch previously never emitted included, which is what left a JWT-registered Backend
        // connection with no signing key and no secret at run time.
        authentication: buildAuthentication(fields, {
          applicationType: athenaAppType,
          scopes: athenaScopes,
          secretSlug: 'athena',
          practiceId: fields['Practice ID'] || null,
        }),
        applicationType: athenaAppType,
        interactive:
          athenaAppType === 'Backend'
            ? null
            : {
                redirectUris: [fields['Redirect URI'] || OAUTH_DEFAULT_URLS.redirectUri],
                launchUrl: fields['Launch URL'] || null,
                trustedIssuers: (fields['Trusted issuers'] ?? '').split(/[\s,]+/).filter(Boolean),
                patientSelectionMethod: null,
                launchDisplayMode: athenaAppType === 'EhrLaunch' ? fields['Launch display mode'] || null : null,
              },
        retrieval: athenaRetrieval,
      };
    }

    // eClinicalWorks (Healow) — same shared Epic-shaped wizard fields (Client ID, FHIR base URL, Scopes, App key,
    // ...), but its own sourceSystemType so the backend's Healow-specific authorize-request handling actually
    // applies (v1-only .read resource scopes, mandatory practice_code derived from the FHIR base URL's last path
    // segment, no offline_access — see SmartAuthorizationCodeTokenProvider.BuildAuthorizationRequest). The canvas
    // node now resolves to NodeType "EClinicalWorksSourceNode" (see workflow-graph-mapper.service.ts's
    // transformIdForNode), so the run executes via EClinicalWorksSourceNodeExecutor and the run's node history
    // names eCW rather than Epic. A workflow saved BEFORE that still carries "EpicSourceNode" and keeps running
    // correctly: EpicSourceNodeExecutor picks EClinicalWorksFhirSourceClient at the HTTP-client-selection step
    // from this SourceSystemType (see SourceNodeExecutors.cs's TrustResolverSourceType). Healow now supports Patient (standalone), Provider EHR launch, AND Backend
    // System (see VENDOR_DISABLED_AUDIENCES) — the authentication block below handles all three (public / jwt /
    // secret), mirroring the Epic branch, so Backend System's private_key_jwt key material is persisted, not dropped.
    if (/healow/i.test(connector)) {
      const healowIsBackend = this.applicationTypeFor(fields) === 'Backend';
      // Resource types (and therefore OAuth scopes) come from what the destination actually maps — see
      // destinationResourceTypesBySourceNodeId in assemble() — exactly as for athenahealth, and for the same
      // reason: eCW fails the WHOLE token request on one unrecognized scope, so a broad guess is worse than a
      // narrow truth. Falls back to whatever the node already held when no destination is wired up yet.
      const healowResourceTypes = destinationResourceTypes.length
        ? destinationResourceTypes
        : (fields['Retrieval resource type'] ?? '').split(',').map((s) => s.trim()).filter(Boolean);
      // eCW does not spell every system/ read scope the same way (ServiceRequest, Coverage, RelatedPerson,
      // Binary, Specimen, MedicationDispense, QuestionnaireResponse, Media and Claim are '.r'-only; the rest are
      // '.read'), and publishes none at all for some resource types. Use the vendor profile rather than a uniform
      // suffix. The backend's SourceConnectionRuntimeResolver regenerates the real scope string from
      // Retrieval.ResourceTypes on every run using its own copy of the same table — this is just what gets
      // initially persisted and validated at build time.
      const healowProfile = vendorScopeProfile('Healow');
      const healowSystemScopes = healowResourceTypes
        .map((rt) => {
          const level = healowProfile?.readAccessLevelByResourceType[rt];
          return level ? `system/${rt}.${level}` : null;
        })
        .filter((scope): scope is string => scope !== null);
      const healowScopes = healowIsBackend
        ? (healowSystemScopes.length
            ? healowSystemScopes
            : (fields['Scopes'] ?? '').split(/[\s,]+/).filter(Boolean))
        : (fields['Scopes'] ?? '').split(/[\s,]+/).filter(Boolean);
      // A Group-level Bulk Data $export needs system/Group.read (to read the Group definition) on top of the
      // per-resource read scopes ScopeBuilderService derives from the selected resource types — otherwise eCW's
      // token omits it and Group/{id}/$export is rejected. eCW's app registration already grants Group.read, so
      // this only asks for what's available. Idempotent: skip if the scope string already carries it.
      if (
        fields['Retrieval method key'] === 'bulk-export' &&
        fields['Export scope'] === 'group' &&
        !healowScopes.includes('system/Group.read')
      ) {
        healowScopes.unshift('system/Group.read');
      }
      const healowAppType = this.applicationTypeFor(fields);
      return {
        name: fields['__name'] || 'eCW',
        sourceSystemType: 'Healow',
        baseUrl: fields['FHIR base URL'] || '',
        // Backend System uses SMART Backend Services (private_key_jwt / RS384); Client Secret uses plain OAuth2
        // client_credentials; Patient/public (PKCE) stores no client credentials at all. This branch was the first
        // to be made auth-method-driven (an earlier version collapsed everything non-'secret' to 'None', silently
        // dropping the Backend audience's JWT key material so token acquisition fell back to client-secret and threw
        // "requires a client id and secret"); buildAuthentication() now generalizes exactly that behaviour to every
        // vendor, and additionally persists jwksUrl — whose host eCW requires to be allow-listed on its own servers,
        // making a later bare invalid_client diagnosable.
        authentication: buildAuthentication(fields, {
          applicationType: healowAppType,
          scopes: healowScopes,
          secretSlug: 'ecw',
        }),
        applicationType: healowAppType,
        // Backend System has no interactive login — mirror the Epic branch (interactive: null for Backend). This is
        // not just cosmetic: EpicSourceConnectionScopeSyncService only rewrites the scopes of connections whose
        // Interactive is non-null, deriving them from the wired destination's resource types (+ auto-fetch reference
        // widening). For eCW that expanded the token request to resource scopes eCW's app registration doesn't grant
        // (Claim/FamilyMemberHistory/Questionnaire) → the whole token request came back invalid_scope. Leaving
        // Interactive null exempts a Backend source from that sync, so it keeps exactly the wizard-configured scopes,
        // same as Epic Backend. The interactive metadata below is only meaningful for EHR-launch / Patient anyway.
        interactive:
          healowAppType === 'Backend'
            ? null
            : {
                redirectUris: [fields['Redirect URI'] || OAUTH_DEFAULT_URLS.redirectUri],
                launchUrl: fields['Launch URL'] || null,
                trustedIssuers: (fields['Trusted issuers'] ?? '').split(/[\s,]+/).filter(Boolean),
                patientSelectionMethod: null,
                launchDisplayMode: healowAppType === 'EhrLaunch' ? fields['Launch display mode'] || null : null,
              },
        // Backend System / Provider Standalone carry a retrieval config (bulk-export or Search REST) just like Epic;
        // without this the connection persisted with null retrieval (method/scope/group), so a re-run or entity-mode
        // edit had to recover it from the workflow node instead. Null for Patient/EHR-launch (no retrieval section).
        retrieval: this.withResourceTypes(
          healowAppType === 'Backend' || healowAppType === 'Standalone'
            ? this.buildRetrieval(fields)
            : null,
          healowResourceTypes,
        ),
      };
    }

    // Epic (best-effort from the Epic source wizard fields).
    const scopes = (fields['Scopes'] ?? '').split(/[\s,]+/).filter(Boolean);
    const discoveredScopes = (fields['Discovered scopes'] ?? '').split(/[\s,]+/).filter(Boolean);
    const appType = this.applicationTypeFor(fields);
    const interactive =
      appType === 'Backend'
        ? null
        : {
            redirectUris: [
              fields['Redirect URI'] ||
                OAUTH_DEFAULT_URLS.redirectUri,
            ],
            launchUrl: fields['Launch URL'] || null,
            trustedIssuers: (fields['Trusted issuers'] ?? '')
              .split(/[\s,]+/)
              .filter(Boolean),
            patientSelectionMethod: null,
            // Only meaningful for EHR launch — the wizard only shows/populates this field for that audience.
            launchDisplayMode:
              appType === 'EhrLaunch'
                ? fields['Launch display mode'] || null
                : null,
          };

    return {
      name: fields['__name'] || 'Epic',
      sourceSystemType: 'Epic',
      baseUrl: fields['FHIR base URL'] || '',
      // Epic's Backend audience is pinned to SmartBackendServices because
      // ConfigurationService.ValidateEpicSourceConnection rejects anything else outright ("A Backend Services Epic
      // source connection must use SMART Backend Services authentication") — so the Auth Method dropdown cannot
      // override it here, and a Client-Secret pick still fails validation exactly as it does from the master path.
      // Every other credential field is auth-method-driven: previously keyId/privateKey* were emitted ungated, so an
      // interactive (PKCE) Epic connection persisted stale key material the master path would have nulled.
      authentication: buildAuthentication(fields, {
        applicationType: appType,
        scopes,
        secretSlug: 'epic',
        forceBackendAuthenticationType: 'SmartBackendServices',
        discoveredScopes: discoveredScopes.length ? discoveredScopes : null,
      }),
      applicationType: appType,
      interactive,
      // Provider Standalone gets a curated Search REST subset too (Resource Types/Search Criteria/Max Results/
      // Include Related Resources — no scheduler, since it's a user-initiated one-shot fetch, not automated).
      retrieval:
        appType === 'Backend' || appType === 'Standalone'
          ? this.buildRetrieval(fields)
          : null,
    };
  }

  private applicationTypeFor(fields: Record<string, string>): string {
    // Key off the exact app key (wizard.service.ts save() always writes 'App key') rather than fuzzy-matching the
    // human-readable context string — "Patient (standalone)" contains the substring "standalone", so checking
    // ctx.includes('standalone') before ctx.includes('patient') silently misclassified every patient-audience
    // connection as Standalone (requesting user/ clinician scopes instead of patient/, which is why Epic rendered
    // Hyperspace instead of MyChart for patient-facing sources).
    switch (fields['App key']) {
      case 'provider-ehr-launch':
        return 'EhrLaunch';
      case 'provider-standalone':
        return 'Standalone';
      case 'patient-standalone':
        return 'Patient';
      case 'backend-system':
        return 'Backend';
    }

    // Fallback for connections saved before 'App key' was captured — patient checked before standalone since
    // "Patient (standalone)" contains "standalone" as a substring.
    const ctx = (
      fields['App context'] ??
      fields['Epic audience'] ??
      ''
    ).toLowerCase();
    if (ctx.includes('ehr')) return 'EhrLaunch';
    if (ctx.includes('patient')) return 'Patient';
    if (ctx.includes('standalone')) return 'Standalone';
    return 'Backend';
  }

  /** Overrides a retrieval config's resourceTypes with the destination-derived list when there is one, leaving
   *  whatever buildRetrieval() already resolved when the graph has no destination wired up yet. Used by the vendor
   *  branches that derive scopes from what the destination actually maps rather than from a wizard guess. */
  private withResourceTypes(
    retrieval: SourceRetrievalConfigurationRequest | null,
    resourceTypes: string[],
  ): SourceRetrievalConfigurationRequest | null {
    if (!retrieval) return null;
    return resourceTypes.length ? { ...retrieval, resourceTypes } : retrieval;
  }

  /** Backend System (full method picker) and Provider Standalone (Search REST subset, one-shot — Run
   *  Mode/scheduler fields are never populated so they simply come through as null/default) — maps the wizard's
   *  Retrieval Configuration fields onto the backend's retrieval DTO. Returns null when no retrieval method was
   *  chosen (e.g. the connection is still being drafted). */
  private buildRetrieval(
    fields: Record<string, string>,
  ): SourceRetrievalConfigurationRequest | null {
    const retrievalMethod = fields['Retrieval method key'];
    if (!retrievalMethod) return null;

    const splitList = (raw: string | undefined): string[] =>
      (raw ?? '')
        .split(',')
        .map((v) => v.trim())
        .filter(Boolean);
    const toPositiveNumber = (raw: string | undefined): number | null => {
      const n = Number(raw);
      return raw && Number.isFinite(n) && n > 0 ? n : null;
    };

    const includeParameters = splitList(fields['Include (_include)']);
    const revIncludeParameters = splitList(
      fields['Reverse include (_revinclude)'],
    );
    // Standalone hides the retrieval method's own Resource Type control and reuses the shared Resource Type &
    // Scopes picker (Section 5, saved under 'Resources') instead — fall back to that when the retrieval-specific
    // one is empty, which it always is for Standalone.
    const retrievalResourceTypes = splitList(fields['Retrieval resource type']);
    const resourceTypes = retrievalResourceTypes.length
      ? retrievalResourceTypes
      : splitList(fields['Resources']);

    // Patient ID list is a free-text area — accept comma- or newline-separated ids.
    const patientIds = (fields['Patient ID / list'] ?? '')
      .split(/[\s,]+/)
      .map((v) => v.trim())
      .filter(Boolean);
    const exportScope = fields['Export scope'] || null;
    // Two retrieval methods carry patient ids: Bulk Export's patient-scoped $export (a list), and Single Patient
    // (at most one id, optional — see RETRIEVAL_METHOD_CONFIG['single-patient']). Both write the same
    // 'Patient ID / list' field, so gate on whichever one is actually selected rather than on exportScope alone,
    // which Single Patient never sets.
    const carriesPatientIds =
      exportScope === 'patient' || retrievalMethod === 'single-patient';
    // The wizard uses short tokens; $export's _outputFormat expects the registered MIME type. Both ndjson variants
    // map to application/fhir+ndjson (gzip is negotiated via transport encoding, not a distinct _outputFormat value).
    const outputFormat = (fields['FHIR output format'] ?? '').startsWith(
      'ndjson',
    )
      ? 'application/fhir+ndjson'
      : null;

    return {
      retrievalMethod,
      resourceTypes,
      searchCriteria: fields['Search criteria'] || null,
      incrementalSyncEnabled: fields['Incremental cursor'] === 'enabled',
      pageSize: toPositiveNumber(fields['Page size (_count)']),
      sortOrder: fields['Sort (_sort)'] || null,
      includeParameters: includeParameters.length ? includeParameters : null,
      revIncludeParameters: revIncludeParameters.length
        ? revIncludeParameters
        : null,
      retryPolicy: fields['Retry policy'] || null,
      timeoutSeconds: toPositiveNumber(fields['Timeout (seconds)']),
      maxRecordsPerRun: toPositiveNumber(fields['Max records per run']),
      // Bulk Data $export settings — only meaningful when retrievalMethod === 'bulk-export'.
      exportScope,
      groupId: exportScope === 'group' ? fields['Group ID'] || null : null,
      patientIds: carriesPatientIds && patientIds.length ? patientIds : null,
      outputFormat,
    };
  }

  // ── destination ─────────────────────────────────────────────────────────────
  private buildDestination(
    fields: Record<string, string>,
    node: WorkflowNodeRequest,
  ): CreateDestinationConfigurationRequest {
    const isMySql =
      node.nodeType.includes('MySql') ||
      (fields['__transformId'] ?? '') === 'dest-mysql';
    const isPostgres =
      node.nodeType.includes('PostgreSql') ||
      (fields['__transformId'] ?? '') === 'dest-postgres';
    const isSql =
      isMySql ||
      isPostgres ||
      node.nodeType.includes('SqlServer') ||
      (fields['__transformId'] ?? '') === 'dest-sqlserver';
    const isMongo =
      node.nodeType.includes('Mongo') ||
      (fields['__transformId'] ?? '') === 'dest-mongo';
    const isMedplum =
      node.nodeType.includes('Medplum') ||
      (fields['__transformId'] ?? '') === 'dest-medplum';
    const isAzureFhir =
      node.nodeType.includes('AzureFhirService') ||
      (fields['__transformId'] ?? '') === 'dest-azurefhir';
    // Excludes isAzureFhir explicitly — 'AzureFhirServiceDestinationNode' itself contains the substring
    // 'Fhir', so the plain node.nodeType.includes('Fhir') check below would otherwise also match it,
    // silently misclassifying an Azure FHIR Service node as a generic FhirRepository (Aidbox) one.
    const isFhir =
      !isAzureFhir &&
      (node.nodeType.includes('Fhir') ||
        (fields['__transformId'] ?? '') === 'dest-fhir');
    const isBlob =
      node.nodeType.includes('Blob') ||
      (fields['__transformId'] ?? '') === 'dest-blob';
    const isDataLake =
      node.nodeType.includes('DataLakeWebhook') ||
      (fields['__transformId'] ?? '') === 'dest-datalake-webhook';
    // Tested BEFORE isFabric, and isFabric excludes it: the Warehouse node type also contains "DataFabric",
    // so an unguarded includes() check would classify a Warehouse node as the file-landing type and write it
    // away as DataFabricAzure — the exact mis-routing splitting the type was meant to make impossible.
    const isFabricWarehouse =
      node.nodeType.includes('DataFabricWarehouse') ||
      (fields['__transformId'] ?? '') === 'dest-fabric-warehouse';
    const isFabric =
      !isFabricWarehouse &&
      (node.nodeType.includes('DataFabric') ||
        (fields['__transformId'] ?? '') === 'dest-fabric');
    const isApiEndpoint =
      node.nodeType.includes('ApiEndpoint') ||
      (fields['__transformId'] ?? '') === 'dest-apiendpoint';
    const name =
      fields['dest_name'] || (isMySql ? 'MySQL Destination' : isPostgres ? 'PostgreSQL Destination' : isSql ? 'SQL Destination' : isMongo ? 'MongoDB Destination' : isMedplum ? 'Medplum Destination' : isFhir ? 'FHIR Repository Destination' : isAzureFhir ? 'Azure FHIR Service Destination' : isBlob ? 'Azure Blob Destination' : isDataLake ? 'Data Lake Webhook Destination' : isFabricWarehouse ? 'Microsoft Fabric Warehouse Destination' : isFabric ? 'Microsoft Fabric Destination' : isApiEndpoint ? 'API Endpoint Destination' : 'File Destination');
    // Reuse the secret reference from a prior build (injected back onto this node's config as secretKeyVaultName/
    // secretName — see WorkflowEndpoints.MapWorkflowEndpoints's Destinations step) so re-saving an existing
    // destination overwrites its ProvisionedSecrets row via WriteSecretAsync's (KeyVaultName, SecretName) upsert
    // instead of minting a brand-new name every save, which only ever inserts a new row and orphans the old one.
    const keyVaultName = fields['secretKeyVaultName'] || 'workflow-secrets';
    const secretName =
      fields['secretName'] || `dest-${this.slug(name)}-${this.shortId()}`;

    // dest_password/dest_sftpPassword are redacted from persisted config (see WorkflowGraphMapperServiceV2's
    // SECRET_FIELD_KEYS) and never round-trip back into the wizard on reload — a blank password field on a
    // destination that's ALREADY been provisioned (it already carries a secretName from a prior build) means
    // "the wizard never had a password to show", not "the user wants to blank out a working credential". Rebuilding
    // the connection string/URI anyway would send a non-blank string with an empty password embedded in it, which
    // the backend's own "don't touch the secret if none was sent" guard (ConfigurationService's
    // UpdateDestinationConfigurationAsync, checking IsNullOrWhiteSpace on the WHOLE string) can't catch — silently
    // overwriting a working credential with a broken one on every no-op re-save. Only rebuild when a password was
    // actually entered, or this is a brand-new destination with nothing to preserve yet.
    const hasExistingSecret = !!fields['secretName'];

    if (isSql) {
      return {
        name,
        destinationType: isMySql ? 'MySql' : isPostgres ? 'PostgreSql' : 'SqlServer',
        keyVaultName,
        secretName,
        target: null,
        inlineSecret:
          hasExistingSecret && !fields['dest_password']
            ? null
            : this.buildSqlConnectionString(fields, isMySql, isPostgres),
        connectionMetadataJson: this.buildConnectionMetadata(fields, 'sql'),
        // Forking off a picked connection (destination-wizard.component.ts's _save() stashes this) never
        // re-populates the password field, so the connection string just built above may be missing
        // credentials the user never meant to change — lets the backend inherit them from the connection
        // being forked from instead of forcing a retype (see ISqlConnectionSecretMerger).
        inheritSecretFromDestinationId: fields['dest_inheritSecretFromDestinationId'] || undefined,
      };
    }

    if (isMongo) {
      return {
        name,
        destinationType: 'Mongo',
        keyVaultName,
        secretName,
        // null, not the primary collection — matches SQL's own `target: null` above. A non-null Target here
        // wins over EVERY resource's own MappingProfile.DestinationObject in MappedMongoDestinationWriter's
        // `destination.Target ?? mappingProfile.DestinationObject` resolution, collapsing every additional
        // collection (added via the mapping canvas's "+ Add a collection" picker) back onto the primary one.
        target: null,
        // The whole connection string is treated as secret (see destination-wizard.component.ts's mongoForm
        // comment) — there's no split server/database/credentials form to assemble from, so this is a direct
        // pass-through of whatever the wizard collected, same "don't touch an already-provisioned secret unless
        // the user actually typed a new one" guard the SQL/SFTP branches use.
        inlineSecret:
          hasExistingSecret && !fields['dest_connectionString']
            ? null
            : fields['dest_connectionString'] || '',
        connectionMetadataJson: this.buildConnectionMetadata(fields, 'mongo'),
      };
    }

    if (isMedplum) {
      return {
        name,
        destinationType: 'Medplum',
        keyVaultName,
        secretName,
        // The FHIR base URL is the destination target; the client secret / PEM private key is treated as the
        // whole opaque secret (see destination-wizard.component.ts's medplumForm) — same "don't touch an
        // already-provisioned secret unless the user actually typed a new one" guard the SQL/SFTP/Mongo branches use.
        target: fields['dest_medplumBaseUrl'] || null,
        inlineSecret:
          hasExistingSecret && !fields['dest_medplumSecret']
            ? null
            : fields['dest_medplumSecret'] || '',
        connectionMetadataJson: this.buildConnectionMetadata(fields, 'medplum'),
      };
    }

    if (isFhir) {
      // dest_clientSecret/dest_password/dest_bearerToken are redacted from persisted config (see
      // WorkflowGraphMapperServiceV2's SECRET_FIELD_KEYS) and never round-trip back into the wizard on reload —
      // same "don't blank an already-provisioned secret unless the user actually typed a new one" guard the
      // SQL/Mongo/SFTP branches above use.
      const hasNewSecretInput = !!(fields['dest_clientSecret'] || fields['dest_password'] || fields['dest_bearerToken']);
      return {
        name,
        destinationType: 'FhirRepository',
        keyVaultName,
        secretName,
        target: fields['dest_baseUrl'] || null,
        inlineSecret:
          hasExistingSecret && !hasNewSecretInput
            ? null
            : this.buildFhirSecretBlob(fields),
        connectionMetadataJson: this.buildConnectionMetadata(fields, 'fhir'),
      };
    }

    if (isAzureFhir) {
      // dest_clientSecret is redacted from persisted config (see WorkflowGraphMapperServiceV2's
      // SECRET_FIELD_KEYS) and never round-trips back into the wizard on reload — same "don't blank an
      // already-provisioned secret unless the user actually typed a new one" guard the FHIR/SQL/Mongo
      // branches above use. Managed identity never resolves a Key Vault secret at all (see
      // FhirRepositoryAuthResolver's "managedidentity" branch server-side) — always sent as '' for that
      // mode, mirroring the Blob branch's managedIdentity handling below.
      const hasNewSecretInput = !!fields['dest_clientSecret'];
      return {
        name,
        destinationType: 'AzureFhirService',
        keyVaultName,
        secretName,
        target: fields['dest_baseUrl'] || null,
        inlineSecret:
          fields['dest_authType'] === 'managedIdentity'
            ? ''
            : hasExistingSecret && !hasNewSecretInput
              ? null
              : this.buildFhirSecretBlob(fields),
        connectionMetadataJson: this.buildConnectionMetadata(fields, 'fhir'),
      };
    }

    if (isBlob) {
      return {
        name,
        destinationType: 'BlobStorage',
        keyVaultName,
        secretName,
        target: fields['dest_blobContainer'] || null,
        // Managed Identity never resolves a Key Vault secret (see BlobDestinationSettings.RequiresSecret
        // server-side) — always sent as '' for that mode, same "don't touch an already-provisioned secret
        // unless the user actually typed a new one" guard the SQL/SFTP branches use otherwise.
        inlineSecret:
          fields['dest_blobAuthMode'] === 'managedIdentity'
            ? ''
            : hasExistingSecret && !fields['dest_blobSecret']
              ? null
              : fields['dest_blobSecret'] || '',
        connectionMetadataJson: this.buildConnectionMetadata(fields, 'blob'),
      };
    }

    if (isDataLake) {
      return {
        name,
        destinationType: 'DataLakeWebhook',
        keyVaultName,
        secretName,
        // The endpoint URL doubles as the target — DataLakeWebhookSettings.Parse reads Target as the
        // fallback for dest_dlwEndpointUrl. Null for auth mode 'none' with a blank endpoint, where the
        // stored secret carries the whole pre-authorized ingest URL instead.
        target: fields['dest_dlwEndpointUrl'] || null,
        // Auth mode 'none' with an endpoint configured resolves no credential at all (see
        // DataLakeWebhookSettings.RequiresSecret) — same shape as Blob's Managed Identity case, and the
        // same "don't overwrite an already-provisioned secret unless the user typed a new one" guard.
        inlineSecret:
          fields['dest_dlwAuthMode'] === 'none' && fields['dest_dlwEndpointUrl']
            ? ''
            : hasExistingSecret && !fields['dest_dlwSecret']
              ? null
              : fields['dest_dlwSecret'] || '',
        connectionMetadataJson: this.buildConnectionMetadata(fields, 'datalake'),
      };
    }

    // Shares every field with the Files branch below — same workspace/item/Entra-auth shape — differing only
    // in the destination type, which is what carries "this is the Warehouse surface" from here on.
    if (isFabricWarehouse) {
      return {
        name,
        destinationType: 'DataFabricWarehouse',
        keyVaultName,
        secretName,
        target: fields['dest_fabricWorkspace'] || null,
        inlineSecret:
          fields['dest_fabricAuthMode'] !== 'servicePrincipal'
            ? null
            : (fields['dest_fabricSecret'] || null),
        connectionMetadataJson: this.buildConnectionMetadata(fields, 'fabric'),
      };
    }

    if (isFabric) {
      return {
        name,
        destinationType: 'DataFabricAzure',
        keyVaultName,
        secretName,
        // The workspace is the target — FabricDestinationSettings.Parse reads Target as the fallback
        // for dest_fabricWorkspace, the same dual read Blob does for its container name.
        target: fields['dest_fabricWorkspace'] || null,
        // Managed identity resolves no Key Vault secret at all (FabricDestinationSettings.RequiresSecret).
        inlineSecret:
          fields['dest_fabricAuthMode'] !== 'servicePrincipal'
            ? ''
            : hasExistingSecret && !fields['dest_fabricSecret']
              ? null
              : fields['dest_fabricSecret'] || '',
        connectionMetadataJson: this.buildConnectionMetadata(fields, 'fabric'),
      };
    }

    if (isApiEndpoint) {
      return {
        name,
        destinationType: 'ApiEndpoint',
        keyVaultName,
        secretName,
        // The endpoint URL doubles as the target — ApiEndpointSettings.Parse reads Target as the fallback
        // for dest_apiEndpointUrl.
        target: fields['dest_apiEndpointUrl'] || null,
        // Auth mode 'none' resolves no credential at all (see ApiEndpointSettings.RequiresSecret) — same
        // "don't overwrite an already-provisioned secret unless the user typed a new one" guard the other
        // HTTP destinations above use.
        inlineSecret:
          fields['dest_apiAuthMode'] === 'none'
            ? ''
            : hasExistingSecret && !fields['dest_apiSecret']
              ? null
              : fields['dest_apiSecret'] || '',
        connectionMetadataJson: this.buildConnectionMetadata(fields, 'apiendpoint'),
      };
    }

    const isSftp = fields['dest_deliveryMode'] === 'sftp';
    return {
      name,
      // Always 'Csv': the delivery mode (download/email/sftp/download-link) is a ConnectionMetadataJson field
      // (dest_deliveryMode), not the DestinationType — previously this always hardcoded 'Sftp' regardless of the
      // chosen delivery mode, producing a broken empty-host sftp:// secret for every other mode.
      destinationType: 'Csv',
      keyVaultName,
      secretName,
      target: fields['dest_filePattern'] || null,
      inlineSecret: !isSftp
        ? ''
        : hasExistingSecret && !fields['dest_sftpPassword']
          ? null
          : this.buildSftpUri(fields),
      connectionMetadataJson: this.buildConnectionMetadata(fields, 'csv'),
    };
  }

  /** Non-secret dest_* fields as a flat JSON object — everything above EXCEPT dest_password/dest_sftpPassword/
   *  dest_connectionString, which only ever live in the encrypted secret (buildSqlConnectionString/buildSftpUri/
   *  the Mongo pass-through), never here. Mirrors destination-connection-secret.util.ts's buildConnectionMetadata
   *  — duplicated rather than imported for the same reason buildSqlConnectionString/buildSftpUri are (see that
   *  file's own header comment). */
  private buildConnectionMetadata(
    f: Record<string, string>,
    kind: 'sql' | 'mongo' | 'csv' | 'medplum' | 'fhir' | 'blob' | 'datalake' | 'fabric' | 'apiendpoint',
  ): string {
    const keys =
      kind === 'apiendpoint'
        ? [
            'dest_name',
            'dest_apiEndpointUrl',
            'dest_apiHttpMethod',
            'dest_apiAuthMode',
            'dest_apiAuthHeaderName',
            'dest_apiKeyQueryParamName',
            'dest_apiSignatureHeaderName',
            'dest_apiTimestampHeaderName',
            'dest_apiTokenEndpoint',
            'dest_apiClientId',
            'dest_apiScope',
            'dest_apiPayloadShape',
            'dest_apiContentType',
            'dest_apiCompression',
            'dest_apiRequireHttps',
            'dest_apiBatchSize',
            'dest_apiMaxRequestBytes',
            'dest_apiTimeoutSeconds',
            'dest_apiRetryCount',
            'dest_apiRetryBackoffSeconds',
            'dest_apiExpectedStatusCodes',
            'dest_apiHeadersJson',
            'dest_apiQueryParamsJson',
            'dest_apiBodyTemplateJson',
            'dest_apiIncludeSourceJson',
            'dest_apiOnFailure',
          ]
        : kind === 'datalake'
        ? [
            'dest_name',
            'dest_dlwEndpointUrl',
            'dest_dlwAuthMode',
            'dest_dlwAuthHeaderName',
            'dest_dlwSignatureHeaderName',
            'dest_dlwTimestampHeaderName',
            'dest_dlwTokenEndpoint',
            'dest_dlwClientId',
            'dest_dlwScope',
            'dest_dlwPayloadShape',
            'dest_dlwHttpMethod',
            'dest_dlwContentType',
            'dest_dlwCompression',
            'dest_dlwBatchSize',
            'dest_dlwMaxRequestBytes',
            'dest_dlwTimeoutSeconds',
            'dest_dlwRetryCount',
            'dest_dlwRetryBackoffSeconds',
            'dest_dlwExpectedStatusCodes',
            'dest_dlwHeadersJson',
            'dest_dlwIncludeSourceJson',
            'dest_dlwOnFailure',
          ]
        : kind === 'fabric'
        ? [
            'dest_name',
            'dest_fabricMode',
            'dest_fabricWorkspace',
            'dest_fabricItemName',
            'dest_fabricItemType',
            'dest_fabricPath',
            'dest_fabricFileFormat',
            'dest_fabricPartitionBy',
            'dest_fabricAuthMode',
            'dest_fabricTenantId',
            'dest_fabricClientId',
            'dest_fabricManagedIdentityClientId',
            'dest_fabricEndpointSuffix',
            'dest_fabricAuthorityHost',
            'dest_fabricAccountUrl',
            // Warehouse landing mode. Allowlist, so a missing key is silently dropped before the save — which
            // made the server reject the request for the very fields the form had just posted.
            'dest_fabricWarehouseSqlEndpoint',
            'dest_fabricWarehouseStagingLakehouse',
            'dest_fabricWarehouseTable',
            'dest_fabricWarehouseSchema',
            'dest_fabricWarehouseWriteMode',
            'dest_fabricWarehouseStagingPath',
          ]
        : kind === 'sql'
        ? [
            'dest_name',
            'dest_server',
            'dest_database',
            'dest_auth',
            'dest_username',
            'dest_schema',
            'dest_writeMode',
            'dest_requireSsl',
          ]
        : kind === 'mongo'
          ? ['dest_name', 'dest_collection', 'dest_writeMode', 'dest_createCollectionIfNotExists']
          : kind === 'medplum'
            ? [
                'dest_name',
                // Carried in metadata as a fallback for Target: the workflow-graph / bulk-export-resume run path
                // reconstructs the destination from node config and can leave Target empty, so the FHIR base URL
                // must also live here for the Medplum writer to resolve it. See MedplumConnectionMetadata.BaseUrl.
                'dest_medplumBaseUrl',
                'dest_medplumClientId',
                'dest_medplumAuthMethod',
                'dest_medplumWriteMode',
                'dest_medplumBatchSize',
                'dest_medplumIdentifierSystem',
              ]
            : kind === 'fhir'
            ? [
                'dest_name',
                'dest_baseUrl',
                'dest_project',
                'dest_writeMode',
                'dest_fhirWriteMode',
                'dest_tokenEndpoint',
                'dest_clientId',
                'dest_username',
                'dest_fhirMapMode',
                'dest_fhirCustomRules',
                // Azure FHIR Service (Azure Health Data Services) — non-secret managed-identity/scope
                // overrides. dest_fhirAuthType itself is set separately, via the dest_authType bridge below.
                'dest_fhirAzureScope',
                'dest_fhirManagedIdentityClientId',
                'dest_autoFetchMissingReferences',
                'dest_autoFetchMaxCount',
              ]
            : kind === 'blob'
              ? [
                  'dest_name',
                  'dest_blobAuthMode',
                  'dest_blobContainer',
                  'dest_blobAccountUrl',
                  'dest_blobAccountName',
                  'dest_blobEndpointSuffix',
                  'dest_blobTenantId',
                  'dest_blobClientId',
                  'dest_blobManagedIdentityClientId',
                  'dest_blobPathPrefix',
                  'dest_blobCreateContainerIfNotExists',
                  'dest_blobGranularity',
                  'dest_blobRecordMode',
                  'dest_blobFolderPattern',
                  'dest_blobFileNamePattern',
                ]
              : [
                'dest_name',
                'dest_deliveryMode',
                'dest_filePattern',
                'dest_delimiter',
                'dest_encoding',
                'dest_sftpHost',
                'dest_sftpPort',
                'dest_sftpUsername',
                'dest_sftpAuthType',
                'dest_sftpRemoteFolder',
                'dest_emailTo',
                'dest_emailCc',
                'dest_emailSubjectTemplate',
                'dest_emailBodyTemplate',
                'dest_downloadLinkExpiryMinutes',
              ];
    const metadata: Record<string, string> = {};
    for (const key of keys) {
      if (f[key] !== undefined) metadata[key] = f[key];
    }
    // The backend reads this metadata key as dest_fhirAuthType (see FhirRepositoryAuthResolver); the wizard's own
    // field/form-control name is dest_authType — bridge the naming difference here rather than renaming either
    // side to match, since dest_authType already mirrors the SQL family's dest_auth naming convention. The wizard's
    // internal value for this option is 'oauth2' (matches its own authType control/validators throughout the
    // component), but the backend's CreateDestinationConfigurationRequestValidator/FhirRepositoryAuthResolver only
    // recognize 'clientCredentials' — bridge the value too, not just the key.
    if (kind === 'fhir' && f['dest_authType'] !== undefined) {
      metadata['dest_fhirAuthType'] = f['dest_authType'] === 'oauth2' ? 'clientCredentials' : f['dest_authType'];
    }
    return JSON.stringify(metadata);
  }

  /** Builds the FHIR-repository destination's encrypted secret blob, shaped to match exactly what the backend's
   *  FhirRepositoryAuthResolver (FHIRBridge.Infrastructure) expects to parse for each auth type. */
  private buildFhirSecretBlob(f: Record<string, string>): string {
    const authType = f['dest_authType'] ?? 'oauth2';
    if (authType === 'basic') {
      return JSON.stringify({ username: f['dest_username'] ?? '', password: f['dest_password'] ?? '' });
    }
    if (authType === 'bearer') {
      return JSON.stringify({ token: f['dest_bearerToken'] ?? '' });
    }
    // Managed identity (Azure FHIR Service) never resolves a Key Vault secret at all — see
    // FhirRepositoryAuthResolver's "managedidentity" branch, which skips secret retrieval entirely for it.
    if (authType === 'managedIdentity') {
      return '';
    }
    return JSON.stringify({
      clientId: f['dest_clientId'] ?? '',
      clientSecret: f['dest_clientSecret'] ?? '',
      tokenEndpoint: f['dest_tokenEndpoint'] ?? '',
      // Only AzureFhirServiceDestinationFormComponent's config carries dest_fhirAzureScope (even blank) — the
      // generic Aidbox/FhirRepository form never collects a scope at all, and its OAuth2 servers have
      // tolerated an omitted scope, so this is deliberately scoped to Azure FHIR Service's config shape only.
      // Entra ID's v2.0 token endpoint requires a non-empty scope for client_credentials (AADSTS90014
      // otherwise) — default to Azure's own {resource}/.default convention when the user left it blank.
      ...(f['dest_fhirAzureScope'] !== undefined
        ? { scope: f['dest_fhirAzureScope'] || `${(f['dest_baseUrl'] ?? '').replace(/\/+$/, '')}/.default` }
        : {}),
    });
  }

  private buildSqlConnectionString(f: Record<string, string>, isMySql = false, isPostgres = false): string {
    const server = f['dest_server'] ?? '';
    const database = f['dest_database'] ?? '';
    const requireSsl = f['dest_requireSsl'] === 'true';
    if (isPostgres) {
      // Npgsql uses Host (not Server) and Username (not User Id); "Require" mode encrypts without validating
      // the server certificate, so no separate "trust cert" flag is needed. Off by default — a local/docker
      // Postgres with SSL disabled would otherwise refuse to connect — checked for providers that enforce it
      // (e.g. AWS RDS's rds.force_ssl).
      return [
        `Host=${server}`,
        `Database=${database}`,
        `Username=${f['dest_username'] ?? ''}`,
        `Password=${f['dest_password'] ?? ''}`,
        `SSL Mode=${requireSsl ? 'Require' : 'Prefer'}`,
      ].join(';');
    }
    const parts = [`Server=${server}`, `Database=${database}`];
    if (isMySql) {
      // MySqlConnector's connection string builder rejects SQL-Server-only keywords
      // (TrustServerCertificate/Encrypt/Authentication=Active Directory Default), so MySQL always
      // authenticates with the username/password entered in the (shared) SQL-family wizard form.
      parts.push(
        `User Id=${f['dest_username'] ?? ''}`,
        `Password=${f['dest_password'] ?? ''}`,
        `SslMode=${requireSsl ? 'Required' : 'Preferred'}`,
      );
      return parts.join(';');
    }
    if ((f['dest_auth'] ?? 'sql-auth') === 'sql-auth') {
      parts.push(
        `User Id=${f['dest_username'] ?? ''}`,
        `Password=${f['dest_password'] ?? ''}`,
      );
    } else {
      parts.push('Authentication=Active Directory Default');
    }
    parts.push('TrustServerCertificate=True', 'Encrypt=True');
    return parts.join(';');
  }

  private buildSftpUri(f: Record<string, string>): string {
    const user = encodeURIComponent(f['dest_sftpUsername'] ?? '');
    const pass = encodeURIComponent(f['dest_sftpPassword'] ?? '');
    const host = f['dest_sftpHost'] ?? '';
    const port = f['dest_sftpPort'] ?? '22';
    const folder = (
      f['dest_sftpRemoteFolder'] ??
      f['dest_folder'] ??
      ''
    ).replace(/^\/+/, '');
    return `sftp://${user}:${pass}@${host}:${port}/${folder}`;
  }

  // ── mapping ───────────────────────────────────────────────────────────────
  // One MappingBuildSpec per resource the destination actually selected — a destination picking Patient +
  // Observation + Condition produces three specs, all sharing the same canvas "Field Mapping" node id (that one
  // node's wizard-authored dest_mappings already carries every resource's field rows, tagged by resource). The
  // backend creates one MappingProfile per spec and accumulates their ids onto that shared node (see
  // WorkflowEndpoints.cs's Mappings step) rather than each overwriting the last — previously only the first
  // (primary) resource ever got a real profile, and every other selected resource silently inherited the
  // primary's field mappings at run time instead of its own (see MappingNodeExecutor).
  private buildMappings(
    mappingNodeId: string,
    sourceNodeId: string,
    destinationNodeId: string,
    destFields: Record<string, string>,
    mappingFields: Record<string, string>,
  ): MappingBuildSpec[] {
    const rows = this.parseMappingRows(destFields['dest_mappings']);
    const resources = [...new Set(rows.map((row) => row.resource))];
    if (resources.length === 0) return [];

    const existingIdsByResource = this.parseExistingMappingProfileIds(mappingFields);

    return resources.map((resource) => {
      const spec = this.buildMappingForResource(
        mappingNodeId,
        sourceNodeId,
        destinationNodeId,
        destFields,
        rows,
        resource,
      );
      return {
        ...spec,
        existingId:
          existingIdsByResource[resource] ??
          // Legacy single-resource node from before mappingProfileIds existed: its one profile id was only ever
          // stored under the flat mappingProfileId field, with no resource tagging at all.
          (resources.length === 1 ? mappingFields['mappingProfileId'] || null : null),
      };
    });
  }

  private parseExistingMappingProfileIds(
    mappingFields: Record<string, string>,
  ): Record<string, string> {
    const raw = mappingFields['mappingProfileIds'];
    if (!raw) return {};
    try {
      const parsed = JSON.parse(raw) as unknown;
      return parsed && typeof parsed === 'object'
        ? (parsed as Record<string, string>)
        : {};
    } catch {
      return {};
    }
  }

  private buildMappingForResource(
    mappingNodeId: string,
    sourceNodeId: string,
    destinationNodeId: string,
    destFields: Record<string, string>,
    rows: DestMappingRow[],
    resource: string,
  ): MappingBuildSpec {
    const resourceRows = rows.filter((row) => row.resource === resource);
    // dest_targets (the wizard's own per-resource "which table is primary" record) is authoritative and
    // must be checked BEFORE resourceRows[0]?.target — now that a row's target correctly reflects its own
    // table (see field-mapping-model.ts's serializeRowsFlat), resourceRows[0] could just as easily be a
    // child-table row as the primary one, and array order here isn't meaningful.
    const baseDestinationObject =
      this.targetForResource(destFields, resource) ||
      resourceRows[0]?.target ||
      resource;
    // The destination wizard's "Write mode" (dw-writeMode) is only ever stashed on dest_writeMode for display —
    // nothing previously translated it into the ;mode=upsert suffix MappedSqlServerDestinationWriter actually
    // reads, so picking "Upsert by source id" in the UI silently still did a blind INSERT. The writer resolves the
    // key column from whichever mapped field is flagged isUpsertKey (the wizard forces this on the resource's
    // mandatory id row — see destination-wizard.component.ts's ID-row reconciliation) rather than a query-string
    // option, so "by source id" only means something once that field is present. jsonPath === '$.id' is kept as a
    // fallback match for mapping rows saved before isUpsertKey existed on a node.
    const idRow =
      resourceRows.find((row) => row.isUpsertKey) ??
      resourceRows.find((row) => (row.jsonPath ?? this.toJsonPath(row.path, resource, row.arrays ?? [])) === '$.id');

    let destinationObject = baseDestinationObject;
    const writeMode = destFields['dest_writeMode'];
    if (writeMode === 'upsert' || writeMode === 'update') {
      if (!idRow) {
        const modeLabel = writeMode === 'upsert' ? 'Upsert by source id' : 'Update only';
        throw new Error(
          `"${resource}" destination is set to ${modeLabel}, but no destination column is mapped from ` +
            `${resource}.id. Map the resource's id field to a column, or switch Write mode to Insert only.`,
        );
      }
      destinationObject = `${baseDestinationObject};mode=${writeMode}`;
    }

    const fields: MappingFieldRequest[] = resourceRows.map((row) => {
      // Prefer the catalog-derived JSONPath/metadata the wizard stamped on the row; fall back to the
      // naive conversion only when the catalog was unavailable.
      const arrays = row.arrays ?? [];
      const jsonPath = row.jsonPath ?? this.toJsonPath(row.path, resource, arrays);
      this.assertArrayWildcardsIntact(resource, row.column, jsonPath, arrays);
      const isArrayPath = jsonPath.includes('[*]') || arrays.length > 0;
      // A row whose own table differs from this resource's baseDestinationObject is a genuine child-table
      // field (e.g. Patient.name.use -> dbo.PatientName) — route it there explicitly via a per-field
      // destinationObject override, same as MappingImportService.BuildFieldAsync does for mapping-profiles
      // /import. Every ordinary same-table field omits this (undefined), keeping the wire payload unchanged
      // from before this existed.
      const isChildTableField = !!row.target && row.target !== baseDestinationObject;
      return {
        targetField: row.column,
        jsonPath,
        ...(isChildTableField ? {
          destinationObject: row.target,
          parentTable: row.parentTable ?? null,
          parentKeyColumn: row.parentKeyColumn ?? null,
          foreignKeyColumn: row.foreignKeyColumn ?? null,
        } : {}),
        // Resolves the field-mapping-list "which resource does this reference?" picker into the referenced
        // resource's own table/id column — without this a FHIR reference field (e.g. Observation.subject.
        // reference) keeps writing the raw "Patient/xyz" string, or NULL, into what's normally a NOT NULL FK
        // column, on every save, regardless of what the user picked in that dropdown.
        ...(row.referencesResource ? this.resolveReferenceLookup(rows, row.referencesResource) : {}),
        // The field-mapping canvas always writes SeparateDestination child-table rows as StoreJson-shaped
        // Json regardless of the naive path-derived type, matching the backend engine's own StoreJson handling.
        valueType: row.arrayPolicy === 'StoreJson' ? 'Json' : (row.valueType ?? this.valueTypeFor(row.path)),
        // The id/Upsert-key row is structurally mandatory (every resource always has an id) — flagging it
        // required here matches reality and satisfies the backend's NOT NULL-vs-IsRequired check
        // (CreateMappingProfileRequestValidator) for destinations whose key column is NOT NULL, which is
        // virtually always the case for a primary key. Every other row defaults to optional; a locked
        // "child of" reference row is upgraded to required separately below. A row carrying its own
        // isRequired (loaded from a profile authored outside the wizard) overrides this computed default.
        isRequired: row.isRequired ?? row === idRow,
        defaultValue: row.defaultValue ?? null,
        format: row.format ?? null,
        normalizationType: row.normalizationType,
        terminologySystemJsonPath: row.terminologySystemJsonPath,
        terminologyCodeJsonPath: row.terminologyCodeJsonPath,
        cardinality: row.cardinality,
        // A flat destination column takes the first match when the path crosses an array; multi-value
        // fan-out (RepeatParent / SeparateDestination) is a deliberate per-field choice, not the default.
        arrayPolicy: row.arrayPolicy ?? (isArrayPath ? 'FirstItem' : 'Scalar'),
        arrayAncestors: arrays.length > 0 ? arrays : null,
        isUpsertKey: row.isUpsertKey ?? row === idRow,
        correlationCodeJsonPath: row.correlationCodeJsonPath ?? null,
        correlationCodeValue: row.correlationCodeValue ?? null,
        isEnabled: row.isEnabled,
      };
    });
    // A locked "child of" row is mandatory the same way the id row is — mark it required so the built
    // request reflects that, even though server-side enforcement (ValidateMappingParentReferences) checks
    // presence/JsonPath match rather than this flag.
    for (const row of resourceRows.filter((r) => r.isRequiredParentRef)) {
      const field = fields.find((f) => f.jsonPath === (row.jsonPath ?? this.toJsonPath(row.path, resource, row.arrays ?? [])));
      if (field) field.isRequired = true;
    }

    const parentReferences = this.parentReferencesFor(destFields, resource);

    return {
      nodeId: mappingNodeId,
      sourceNodeId,
      destinationNodeId,
      name: `${resource} mapping`,
      resourceType: resource,
      destinationObject,
      fields,
      parentReferences: parentReferences.length ? parentReferences : undefined,
    };
  }

  // Reads the wizard's per-resource "child of" chip selections (dest_parentSelections: Record<child,
  // parent[]>) and turns them into the ParentReferenceSpec[] the backend validates against sibling specs
  // sharing the same destination node.
  private parentReferencesFor(destFields: Record<string, string>, resource: string): ParentReferenceSpec[] {
    try {
      const parsed = JSON.parse(destFields['dest_parentSelections'] ?? '{}') as Record<string, string[]>;
      const parents = parsed[resource] ?? [];
      return parents.map((parentResourceType) => ({ parentResourceType }));
    } catch {
      return [];
    }
  }

  private parseMappingRows(json: string | undefined): DestMappingRow[] {
    if (!json) return [];
    try {
      const parsed = JSON.parse(json) as DestMappingRow[];
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  }

  private targetForResource(
    fields: Record<string, string>,
    resource: string,
  ): string | null {
    try {
      const targets = JSON.parse(fields['dest_targets'] ?? '{}') as Record<
        string,
        string
      >;
      return targets[resource] ?? null;
    } catch {
      return null;
    }
  }

  /**
   * Fallback FHIR element path → JSONPath used only when the wizard could not stamp a catalog-derived
   * path on the row. Strips the leading "Resource." and prefixes "$.".
   *
   * `arrays` carries the row's array-ancestor chain (relative to the resource, e.g. ["name"],
   * ["code.coding"]) and MUST be applied here: a row reloaded from a saved Mapping document keeps its
   * arrays (applyMappingSummaryDocument reconstructs them from the column's arrayContext) but NOT its
   * jsonPath — that field is not part of the summary schema — so every such row lands on this fallback on
   * the very next save. Without stamping "[*]" back on, "Patient.name.text" (arrays ["name"]) degraded to
   * "$.name.text", which matches nothing against an array-valued `name`; the Mapping node then skipped the
   * field entirely — no value, no lineage row, no error, run reports Succeeded — and the destination column
   * silently went NULL. Each save→reload cycle re-applied the degradation, which is why a field could work
   * once and then stop the moment any unrelated field was added to the same mapping node.
   */
  private toJsonPath(path: string, resourceType: string, arrays: readonly string[] = []): string {
    let p = path.trim();
    if (p.startsWith(`${resourceType}.`)) p = p.slice(resourceType.length + 1);
    if (p.startsWith('$')) return p;
    // The resource's own ROOT node ("Patient", with nothing after it) is what a whole-node-as-JSON mapping
    // of the entire payload carries — field-mapping-model's serializeRowsFlat writes the group's node id as
    // the row's path, and for the root that id is just the resourceType. Only "$" means "the whole document"
    // to JsonMappingEngine.ResolveAll; the "$.{p}" fallback below would produce "$.Patient", which resolves
    // to nothing and writes NULL into the target column on every record.
    if (!p || p === resourceType) return '$';
    return `$.${this.applyArrayWildcards(p, arrays, resourceType)}`;
  }

  /**
   * Guards the one invariant that ties a field's jsonPath to its array metadata: every declared array
   * ancestor must appear wildcarded in the path. A field that declares `name` an array ancestor while its
   * path reads "$.name.text" addresses nothing at run time, and the Mapping node skips it without writing a
   * value, a lineage row, or an error — so the destination column silently goes NULL on a run that reports
   * Succeeded. That failure is invisible from the UI, which is why it is asserted here at build time rather
   * than left to be discovered by querying the destination. Warn-only: a mapping is never blocked from
   * saving over this, since the path may legitimately come from a catalog whose conventions differ.
   */
  private assertArrayWildcardsIntact(
    resource: string, column: string, jsonPath: string, arrays: readonly string[],
  ): void {
    if (arrays.length === 0 || jsonPath.startsWith('@') || jsonPath === '$') return;
    const missing = arrays
      .map(a => (a.startsWith(`${resource}.`) ? a.slice(resource.length + 1) : a))
      .map(a => a.replace(/\[\*\]/g, '').trim())
      .filter(a => a.length > 0 && !jsonPath.includes(`${a}[*]`));
    if (missing.length > 0) {
      console.warn(
        `[mapping] ${resource}.${column}: jsonPath "${jsonPath}" does not wildcard its array ancestor(s) ` +
        `${missing.join(', ')} — this field will resolve to nothing and write NULL.`,
      );
    }
  }

  /**
   * Stamps "[*]" onto each array ancestor inside a resource-relative element path, so the result addresses
   * the same elements the backend catalog's own jsonPath would ("name" + "name.text" → "name[*].text").
   * Ancestors are applied longest-first so a nested chain (["code", "code.coding"]) can't have a shorter
   * prefix rewritten out from under a longer one. An ancestor that is already wildcarded, or that isn't a
   * prefix of this path, is left alone.
   */
  private applyArrayWildcards(relativePath: string, arrays: readonly string[], resourceType: string): string {
    if (arrays.length === 0) return relativePath;
    let out = relativePath;
    const ancestors = arrays
      .map(a => (a.startsWith(`${resourceType}.`) ? a.slice(resourceType.length + 1) : a))
      .map(a => a.replace(/\[\*\]/g, '').trim())
      .filter(a => a.length > 0)
      .sort((a, b) => b.length - a.length);
    for (const ancestor of ancestors) {
      if (out === ancestor) {
        out = `${ancestor}[*]`;
      } else if (out.startsWith(`${ancestor}.`)) {
        out = `${ancestor}[*]${out.slice(ancestor.length)}`;
      }
    }
    return out;
  }

  private valueTypeFor(path: string): string {
    // Checked against the original (not lowercased) path: FHIR's choice-type fields spell out their type as
    // a capitalized suffix (deceasedBoolean, multipleBirthBoolean, valueBoolean, ...), and "active" is FHIR's
    // other common bare boolean field (Patient.active, Practitioner.active, Location.active, ...). Without this,
    // both fell through to the 'String' default below despite the backend catalog itself typing them Boolean.
    if (/Boolean$/.test(path) || /(^|\.)active$/i.test(path)) return 'Boolean';
    const p = path.toLowerCase();
    if (
      p.includes('birthdate') ||
      p.includes('onset') ||
      /date($|[^t])/.test(p)
    )
      return 'Date';
    if (
      p.includes('datetime') ||
      p.includes('period') ||
      p.includes('effective')
    )
      return 'DateTime';
    return 'String';
  }

  /**
   * Resolves a "which resource does this reference?" picker value (row.referencesResource) into the referenced
   * resource's own destination table + the column its own "$.id" field targets. Mirrors
   * field-mapping-summary.model.ts's computeResourceKeyInfo — that copy only feeds dest_mapping_summary_v1's
   * UI-redisplay round-trip; this is the one that actually reaches MappingFieldRequest.referenceLookupTable/
   * referenceLookupKeyColumn, which JsonMappingEngine/MappedSqlServerDestinationWriter read at pipeline-run
   * time to resolve a raw FHIR reference string into the referenced row's real key at write time. Returns {}
   * (never throws) when the referenced resource has no id row yet — an incomplete save shouldn't crash, it
   * should just leave the reference unresolved, same as if the picker had never been touched.
   */
  private resolveReferenceLookup(
    rows: DestMappingRow[],
    referencedResource: string,
  ): { referenceLookupTable?: string; referenceLookupKeyColumn?: string } {
    const idRow = rows.find(
      (r) => r.resource === referencedResource
        && (r.jsonPath ?? this.toJsonPath(r.path, referencedResource, r.arrays ?? [])) === '$.id',
    );
    return idRow ? { referenceLookupTable: idRow.target, referenceLookupKeyColumn: idRow.column } : {};
  }

  // ── graph helpers ───────────────────────────────────────────────────────────
  private isSourceNode(node: WorkflowNodeRequest): boolean {
    return node.category === 0 || node.category === 'Source';
  }

  private isDestinationNode(node: WorkflowNodeRequest): boolean {
    return node.nodeType.endsWith('DestinationNode');
  }

  private mappingNodeFeeding(
    destNodeId: string,
    graph: WorkflowBuildRequest,
  ): string | null {
    const edge = graph.edges.find((e) => e.toNodeId === destNodeId);
    return edge?.fromNodeId ?? null;
  }

  private sourceFeeding(
    startNodeId: string,
    graph: WorkflowBuildRequest,
    sourceNodeIds: Set<string>,
  ): string | null {
    let current: string | null = startNodeId;
    const seen = new Set<string>();
    while (current && !seen.has(current)) {
      if (sourceNodeIds.has(current)) return current;
      seen.add(current);
      current =
        graph.edges.find((e) => e.toNodeId === current)?.fromNodeId ?? null;
    }
    // Fallback: the first source in the graph (linear single-source pipelines).
    return [...sourceNodeIds][0] ?? null;
  }

  private fieldsFor(
    nodeId: string,
    nodesById: Map<string, WorkflowNodeRequest>,
  ): Record<string, string> {
    // Prefer the live store node fields; fall back to the serialized config on the graph node.
    const storeNode = this.store.byId(nodeId);
    if (storeNode) return storeNode.fields;
    const node = nodesById.get(nodeId);
    if (!node?.configurationJson) return {};
    try {
      const parsed = JSON.parse(node.configurationJson) as Record<
        string,
        unknown
      >;
      return Object.fromEntries(
        Object.entries(parsed).map(([k, v]) => [
          k,
          typeof v === 'string' ? v : JSON.stringify(v),
        ]),
      );
    } catch {
      return {};
    }
  }

  private slug(value: string): string {
    return (
      value
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, '-')
        .replace(/^-+|-+$/g, '')
        .slice(0, 24) || 'dest'
    );
  }

  private shortId(): string {
    return Math.random().toString(36).slice(2, 8);
  }
}
