# Rebuilding the 194-rule catalog (rare — only on a genuinely fresh tenant/environment)

Only do this if `GET /api/v1/transformation-rules?resourceType=Patient` (and a couple of other spot-checks)
come back empty. This catalog is tenant-wide state, not workflow-specific, so it should exist exactly once
per environment — don't recreate it per workflow provisioning run.

## Shape

- **20 Field-scoped rules** on `Patient`/`Observation`/`Encounter`/`Condition` (10/7/2/1), each with
  `scope: "Field"`, the exact `resourceType` + `destinationField` name used by the reference workflow's
  mapping profiles (see `rebuild-template.md` step 3), `resourcePipelineRouteId: null`.
- **174 ResourceType-scoped rules** on the other 32 types (Binary excluded — no applicable field), each
  with `scope: "ResourceType"`, `resourceType` set, `destinationField: null` (this is what makes it a
  workflow-wide default — see `EfTransformationRuleRepository.GetResourceTypeScopedAsync`, which matches
  on resource type alone when `destinationField` is null on the row).
- Every rule uses one of the 20 `TransformNodeType` values from `resource-types.md`, distributed to each
  resource type it's clinically/structurally valid for (e.g. `IdentifierFormatting` on anything with a
  FHIR `identifier` field, `DateMathAge` only on `Patient`/`RelatedPerson`, `HashingMasking` only on the
  two PHI-bearing person resources). If you have to re-derive this distribution, the published artifact
  from the authoring session has the full resource → rule-type table; ask the user for the link first
  rather than re-deriving it from scratch.

## Config values

Use one canonical `config` per node type (flat `Dictionary<string,string>`, JSON-serialize any nested
value like `map` as a string), reused across every resource type it appears on. `sample-values.md` has a
matching sample input per node type, already vetted against the real engine (in particular: don't use
`555`-exchange phone numbers, and remember `CodeableConceptBuilder.system` should be a real code system —
LOINC/SNOMED/RxNorm/CVX/CPT/ICD10 — matched to what that resource type would actually carry, not the
same system for every resource).

## Mechanics

Batch these via a script (`POST /api/v1/transformation-rules` once per rule, `X-CSRF-Token` header
required) rather than one-by-one tool calls — 194 individual approvals is not a good use of anyone's time.
Verify a handful with `GET /api/v1/transformation-rules?resourceType=<X>` afterward rather than trusting
194 "200 OK"s blindly.
