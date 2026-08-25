# Terminology Server POC

Standalone proof-of-concept for the "embed a HAPI FHIR terminology server instead of
homegrown importers" migration discussed for the Terminology subsystem. Nothing in the
existing FHIRBridge codebase (Infrastructure, Api, Worker, docker-compose services other
than the two added below) has been changed. This is new, additive code only.

## What this proves

- A second, dedicated HAPI FHIR instance (`hapi-terminology` + `hapi-terminology-postgres`)
  can run alongside the existing `hapi-fhir`/`hapi-postgres` pair without conflict.
- A code system can be loaded into it over its standard FHIR REST API (`PUT /CodeSystem/{id}`).
- Codes in that system are then queryable via the standard `$lookup` operation — the same
  operation FHIRBridge's existing `FhirTerminologyLookupService` already calls today when
  `Terminology:BaseUrl` is configured.

## What this does NOT prove

- This loads a **10-code demo subset of ICD-10-CM**, not the full official code system
  (~70,000+ codes). Production loading of the real ICD-10-CM order file, or LOINC/SNOMED/
  RxNorm releases, is a separate step — typically via HAPI's own `hapi-fhir-cli
  upload-terminology` tool, which knows how to parse those official file formats. That step
  needs the actual release files (public download for ICD-10; a LOINC account or UMLS/UTS
  key for LOINC/SNOMED/RxNorm) and was out of scope for this POC.
- It does not touch FHIRBridge's existing `ITerminologyLookupService` /
  `CompositeTerminologyLookupService` / DI wiring — those are unmodified. Wiring the real
  services to actually use this server is a follow-up step once the approach is validated.

## Run it

1. Start just the two new containers (leaves everything else in the compose stack alone):

   ```
   docker compose up -d hapi-terminology-postgres hapi-terminology
   ```

2. Wait ~20-30 seconds for HAPI to finish starting (first boot is slower — it's initializing
   its own schema in `hapi-terminology-postgres`).

3. Run the POC from the repo root:

   ```
   dotnet run --project tools/TerminologyServerPoc
   ```

   By default it targets `http://localhost:8090/fhir` (the `hapi-terminology` container's
   published port). Override with an argument or env var if needed:

   ```
   dotnet run --project tools/TerminologyServerPoc -- http://localhost:8090/fhir
   ```

## Manual testing / verification steps

The console output already walks through steps 1-3 below. To double check independently
(e.g. from a browser, curl, or Postman) that the data really landed in the terminology
server's own database:

1. **Confirm the server is up:**
   ```
   curl http://localhost:8090/fhir/metadata
   ```
   Expect a large `CapabilityStatement` JSON response, not a connection error.

2. **Confirm the CodeSystem resource itself was stored:**
   ```
   curl http://localhost:8090/fhir/CodeSystem/icd10cm-demo-subset
   ```
   Expect a `CodeSystem` resource back containing all 10 `concept` entries.

3. **Confirm an individual code is queryable via the standard terminology operation**
   (this is the same call FHIRBridge's pipeline would make at runtime):
   ```
   curl "http://localhost:8090/fhir/CodeSystem/\$lookup?system=http://hl7.org/fhir/sid/icd-10-cm&code=E11.9"
   ```
   Expect a `Parameters` resource whose `display` parameter reads
   `"Type 2 diabetes mellitus without complications"`.

4. **Confirm an unknown code correctly returns nothing** (proves it's really checking the
   loaded data, not just echoing back anything you ask):
   ```
   curl "http://localhost:8090/fhir/CodeSystem/\$lookup?system=http://hl7.org/fhir/sid/icd-10-cm&code=ZZZ.99"
   ```
   Expect an `OperationOutcome` error / non-200 response, not a display value.

5. **Confirm the data survives a container restart** (proves it's really in the Postgres
   volume, not in-memory):
   ```
   docker compose restart hapi-terminology
   ```
   then repeat step 3 — the lookup should still succeed without re-running the POC.

## Cleanup

```
docker compose down
docker volume rm fhirbridge_hapi-terminology-postgres-data
```
