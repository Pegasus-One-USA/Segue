---
name: fhirbridge-provision-coverage-workflow
description: >
  Provisions a brand-new, fully independent FHIRBridge workflow from user-supplied source and destination
  connection settings (source: FHIR base URL, token endpoint, client ID, key ID, JWKS URL / signing key
  reference, environment; destination: SQL server, database, username, password, schema) — then runs the
  full "36-resource-type coverage" flow: clone a verified reference workflow (never build node JSON from
  scratch), patch in the caller's real credentials, verify each save actually persisted (mapping profile
  resourceType, destination connection string, workflow mapping node), confirm the live de-identification
  node and Search REST ingestion mode, generate real before/after values per transformation rule via the
  `/transformation-rules/preview` API, and publish a field-lineage-style HTML report as a Claude artifact.
  Trigger whenever the user asks to "create a new FHIRBridge workflow/connection with these credentials",
  "provision a source and destination and run the coverage flow", "set up a new EHR connection and generate
  the transform report", or similarly describes standing up a new end-to-end FHIRBridge pipeline from raw
  connection settings rather than reusing dev/demo credentials already in this session.
---

# FHIRBridge: Provision a Full-Coverage Workflow from Connection Settings

This skill reproduces, end-to-end and parameterized, the workflow-provisioning + transformation-rule
verification flow built interactively in earlier sessions (see project memory `fhirbridge_*` entries if
present). It does **not** hand-build a workflow's node JSON from scratch — that path is fragile (three
separate real bugs were hit and fixed doing it that way: mapping not reflecting, resource-type selection
not persisting, SQL connection string not saving). Instead it **clones a known-good reference workflow**
and patches in the caller's real credentials, which sidesteps all three failure modes because the clone
endpoint deep-copies every dependent entity (source connection, destination, mapping profiles) as its own
fresh, independently-saved row.

## 0. Required inputs

Ask the user (via AskUserQuestion, one question per missing group) for whatever isn't already supplied.
**Never guess a password, private key, or client secret — always get it from the user directly.** A
missing optional field (e.g. JWKS URL when a private key is uploaded instead) is fine to leave blank.

**Portal/API access** (skip if already authenticated this session):
- API base URL (default `http://localhost:5000`)
- Login email + password for a user with `sourceconnections.create`, `destinationconnections.*`, `workflow.*`, and `mappingprofiles.*` permissions

**Source connection** (SMART Backend Services is the default/most common case):
- `name` — display name for the new source connection
- `vendor` — Epic / Cerner / Athenahealth / generic FHIR / etc. (`SourceSystemType` enum)
- `fhirBaseUrl`, `tokenEndpoint`
- `environment` — sandbox / production (informational, goes in a display field, not a DTO field)
- `clientId`
- `keyId` — the `kid` FHIRBridge should sign backend-services JWTs with
- `jwksUrl` — where the EHR fetches FHIRBridge's public key from (optional — omit if the EHR was given the key another way)
- Signing key: either **(a)** the user already has a key provisioned in FHIRBridge under a known Key Vault name/secret name, or **(b)** ask whether to generate a new one (check for `ISigningKeyGenerationService` / a `/source-connections/.../generate-key`-style endpoint under `SourceDiscoveryController` or similar before assuming — do not fabricate an endpoint)
- Resource types to pull (default: reuse the same 37-type list documented in `reference/resource-types.md`)

**Destination connection** (SQL Server is the default/most common case):
- `name`
- `server` (e.g. `localhost,1433`), `database`, `schema` (default `dbo`)
- `username`, `password`
- `writeMode` (default `upsert`)

**Workflow**:
- `workflowName` — must be unique; the clone endpoint rejects a blank name only, but a duplicate name will
  just create a second workflow with the same display name, so check `GET /api/v1/workflows` first and ask
  the user to confirm if a name collision exists.

## 1. Authenticate

```bash
curl -s -c cookies.txt -X POST $API/api/v1/auth/internal/login \
  -H "Content-Type: application/json" \
  -d '{"email":"<email>","password":"<password>"}'
CSRF=$(grep fhirbridge_csrf cookies.txt | awk '{print $NF}')
```
Every subsequent state-changing call needs both `-b cookies.txt` (session) **and**
`-H "X-CSRF-Token: $CSRF"` (double-submit CSRF header) or it 400s with "CSRF token missing or invalid."

If the saved/known dev credentials fail, don't retry blindly — ask the user for working ones (a DB reset or
branch switch can invalidate old accounts; this happened in a prior session).

## 2. Find (or bootstrap) the reference workflow to clone

Look for a workflow named **"Segue Epic Full Coverage (Search REST)"** via `GET /api/v1/workflows`. This is
the known-good 4-node template (`EpicSourceNode → DeIdentificationNode → MappingNode → SqlServerDestinationNode`,
`retrievalMethod: search-rest`, Safe-Harbor de-identification wired live) built and verified in the session
that authored this skill. If it exists, note its id as `TEMPLATE_ID`.

If it does **not** exist (fresh environment), rebuild it once by following `reference/rebuild-template.md`,
then proceed. Don't rebuild it from scratch every invocation — clone it every time instead.

## 3. Clone the template

```bash
curl -s -X POST $API/api/v1/workflows/$TEMPLATE_ID/copy \
  -b cookies.txt -H "Content-Type: application/json" -H "X-CSRF-Token: $CSRF" \
  -d "{\"name\":\"$WORKFLOW_NAME\"}"
```

This one call creates brand-new, independent rows for: the source connection (Epic auth config, minus
secrets which get their own cloned Key Vault entries), the destination configuration (SQL Server config +
a fresh secret), and all 4 mapping profiles (Patient/Observation/Encounter/Condition) — plus the new
workflow definition wiring them together. Capture every id from the response:
`NEW_WORKFLOW_ID`, the source connection id (from the `EpicSourceNode`/equivalent node's
`configurationJson.sourceConnectionId`), the destination id (`SqlServerDestinationNode`'s `destinationId`),
and the 4 `mappingProfileIds`.

## 4. Patch in the caller's real credentials

**Source** — `PUT /api/v1/source-connections/{sourceConnectionId}` with a full
`CreateSourceConnectionRequest` body (PUT replaces, so re-send fields you're not changing too — GET the
connection first and merge):
```json
{
  "name": "<name>",
  "sourceSystemType": "Epic",
  "baseUrl": "<fhirBaseUrl>",
  "authentication": {
    "authenticationType": "SmartBackendServices",
    "clientId": "<clientId>",
    "tokenEndpoint": "<tokenEndpoint>",
    "scopes": ["system/Patient.rs", "..."],
    "keyId": "<keyId>",
    "jwksUrl": "<jwksUrl or omit>",
    "privateKeyKeyVaultName": "<only if reusing an already-provisioned key>",
    "privateKeySecretName": "<...>"
  },
  "applicationType": "Backend",
  "retrieval": {
    "retrievalMethod": "search-rest",
    "resourceTypes": ["Patient", "..."]
  }
}
```
Keep `retrievalMethod` as `"search-rest"` — this is the explicit requirement to use FHIR Search REST, not
Bulk Export (`retrievalMethod: "bulk-export"` is the other value; never use it here unless the user asks).

**Destination** — `PUT /api/v1/destinations/{destinationId}`:
```json
{
  "name": "<name>",
  "destinationType": "SqlServer",
  "keyVaultName": "workflow-secrets",
  "secretName": "<keep the cloned secretName from step 3 — don't invent a new one>",
  "inlineSecret": "Server=<server>;Database=<database>;User Id=<username>;Password=<password>;",
  "connectionMetadataJson": "{\"dest_server\":\"<server>\",\"dest_database\":\"<database>\",\"dest_username\":\"<username>\",\"dest_schema\":\"<schema>\",\"dest_writeMode\":\"upsert\",\"dest_engine\":\"sqlserver\"}"
}
```
`inlineSecret` non-null tells `UpdateDestinationConfigurationAsync` to re-provision the Key Vault secret
with these real credentials rather than leaving the cloned placeholder in place.

## 5. Verify — do not skip this, it's the whole point

Re-`GET` every entity you just wrote, independently of the write response:

- `GET /api/v1/source-connections/{id}` → confirm `baseUrl`, `authentication.clientId`,
  `authentication.keyId`, `retrieval.retrievalMethod == "search-rest"` all match what you sent.
- `GET /api/v1/destinations` (list — there is no working `GET /destinations/{id}`, that 404s even though
  it's listed in the frontend's endpoint map; use the list and filter by id) → confirm
  `connectionMetadataJson` shows the real server/database/username and `secretName` is set.
- `GET /api/v1/mapping-profiles/{id}` for each of the 4 → confirm `resourceType` is exactly
  `Patient`/`Observation`/`Encounter`/`Condition` (not null, not swapped).
- `GET /api/v1/workflows/{NEW_WORKFLOW_ID}` → confirm the `MappingNode`'s `configurationJson.mappingProfileIds`
  has all 4 entries, the `EpicSourceNode`'s `configurationJson.sourceConnectionId` matches, and a
  `DeIdentificationNode` sits between source and mapping in the `edges` list.

If any of these come back wrong, **stop and report it** — don't paper over it by re-issuing more writes.

## 6. Confirm the rule catalog already covers this workflow

The 20-rule catalog (194 rules across 36 of 37 resource types: 20 Field-scoped on
Patient/Observation/Encounter/Condition + 174 ResourceType-scoped on everything else, per
`reference/resource-types.md`) lives in the database at **Field** and **ResourceType** scope — neither tier
is tied to a specific `ResourcePipelineRouteId`, so it automatically applies to *any* workflow whose mapping
profiles use the same destination field names (Field tier) or resource type (ResourceType tier, which
matches on resource type alone regardless of field name — see `IEffectiveRuleResolver`). Since step 3
cloned the mapping profiles verbatim, the field names match exactly and **no rule needs to be recreated.**

Confirm this rather than assume it: `GET /api/v1/transformation-rules?resourceType=Patient` (and spot-check
one or two others) and check the count matches what's expected. If the catalog is missing entirely (fresh
tenant/environment with no prior work), rebuild it via `reference/rule-catalog-rebuild.md` before continuing.

## 7. Generate real before/after values

Reuse the `/api/v1/transformation-rules/preview` approach: for each of the 20 Field-scoped rules, preview
directly using the real `resourceType`/`destinationField`. For each ResourceType-scoped rule, create a
temporary uniquely-named Field-scope shadow rule (`destinationField: "__preview_<resourceType>_<nodeType>"`),
preview through it, then delete it — this isolates one rule's real output instead of running that resource
type's whole rule chain at once (ResourceType-scope rows share one resolver chain per resource type, so
previewing without isolation garbles multi-rule resources). `reference/sample-values.md` has the vetted
sample input per node type — reuse it, **don't reuse `555-`-exchange phone numbers**, real phone validation
(libphonenumber) rejects them as invalid.

**Known limitation** (already found and shouldn't be rediscovered): `sampleValue` is bound as a bare
`object?`, so a JSON array arrives as a single non-enumerable `JsonElement`, and
`ConcatenationTemplating`/`ArrayListOperations` previews on array input silently no-op. Hand-compute those
rows from the documented op semantics (join/concat with the configured separator) instead, and label them
`computed offline` in the report rather than presenting a no-op as real output.

## 8. Build and publish the report

Load the `artifact-design` skill before writing the HTML (required for any artifact). Use
`reference/report_template.html` as the starting structure — it already has the theme-aware CSS, the
filterable rules table, and the bug-verification checklist; swap in the new workflow's identity, real
before/after rows (as a `<script type="application/json">` blob, same shape as the reference), and the
verification results from step 5. Publish via the Artifact tool with a stable, distinctive title — do not
reuse a previous session's title if this is a genuinely different workflow/environment.

## Things that go wrong if you skip a step

- Building workflow node JSON by hand instead of cloning → this is exactly how the three original bugs
  happened. Always clone.
- Trusting a POST/PUT response body instead of re-`GET`ing → this is how "saved" data turns out not to have
  actually saved. Always verify per step 5.
- Reusing the same Key Vault `secretName` across two different destinations, or inventing a new one instead
  of reusing the clone's → either orphans a secret or silently points two destinations at the same
  credentials. Always reuse the cloned `secretName` in step 4's destination PUT.
- Previewing an array-scoped rule without noticing the `JsonElement` limitation → produces a "before ==
  after, nothing happened" row that reads as a broken rule when it's actually a preview-endpoint quirk.
