# Rebuilding the reference workflow from scratch (rare — only if it's genuinely missing)

Only do this if `GET /api/v1/workflows` has no workflow named "Segue Epic Full Coverage (Search REST)"
(or similar) to clone. This is the manual path that originally surfaced the three bugs this skill exists
to avoid — go slowly and verify every step.

1. **Source connection** — `POST /api/v1/source-connections` with a `CreateSourceConnectionRequest` body:
   `sourceSystemType: "Epic"`, `authentication.authenticationType: "SmartBackendServices"`,
   `retrieval.retrievalMethod: "search-rest"`, `retrieval.resourceTypes` = the 37-type list from
   `resource-types.md`. Use placeholder/sandbox credentials if the real ones aren't available yet — they
   get patched in step 4 of SKILL.md on every actual provisioning run anyway.

2. **Destination** — `POST /api/v1/destinations` with a `CreateDestinationConfigurationRequest`:
   `destinationType: "SqlServer"`, `inlineSecret` set to a real connection string so a Key Vault secret
   gets provisioned, `connectionMetadataJson` with `dest_server`/`dest_database`/`dest_username`/
   `dest_schema`/`dest_writeMode`/`dest_engine`.

3. **Mapping profiles** — `POST /api/v1/mapping-profiles`, one each for Patient/Observation/Encounter/
   Condition, each with `destinationObject` pointing at a `dbo.TxDemo_<Type>;mode=upsert`-style table and
   a `fields` array. Reuse the field lists from the artifact published in the session that authored this
   skill if you can find it (ask the user, or check `MEMORY.md` for a link) — recreating them from scratch
   risks subtly different `jsonPath`/`arrayPolicy` values than what the transformation rules below expect.

4. **Workflow** — `POST /api/v1/workflows` (or `/workflows/build`) with 4 nodes: `EpicSourceNode` (rank 0),
   `DeIdentificationNode` (rank 30, config `{"deIdentify":"true","profileId":"<Safe Harbor profile id>"}`),
   `MappingNode` (rank 60, `mappingProfileIds` = the 4 ids from step 3), `SqlServerDestinationNode`
   (rank 70). Edges: Source→DeIdentification→Mapping→Destination.

5. Verify every entity via `GET`, same as SKILL.md step 5, before treating this as the new template.

If `rule-catalog-rebuild.md` also applies (the 194-rule catalog is missing too), do that *after* this,
since the rule catalog's Field-scope rows key off these mapping profiles' exact destination field names.
