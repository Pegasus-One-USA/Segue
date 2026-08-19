# Dependency / Creation Order

When resources are uploaded **individually** (one request per resource) against a server that
enforces referential integrity (HAPI, Aidbox), each resource's referenced targets must already
exist. Create resources in this order:

```
 1. Organization        (no outbound refs)
 2. Location            -> Organization (managingOrganization)
 3. Practitioner        (no outbound refs)
 4. PractitionerRole    -> Practitioner, Organization, Location
 5. Device              -> Patient*      (*see note)
 6. Medication          (no outbound refs)
 7. Patient             -> Organization (managingOrganization)
 8. RelatedPerson       -> Patient
 9. Coverage            -> Patient (beneficiary), Organization (payor)
10. CareTeam            -> Patient, Practitioner, Encounter
11. Encounter           -> Patient, Practitioner, Organization, Location
12. Condition           -> Patient, Encounter
13. AllergyIntolerance  -> Patient
14. Observation         -> Patient, Encounter
15. DiagnosticReport    -> Patient, Encounter, Observation (result[])
16. Procedure           -> Patient, Encounter, Practitioner (performer.actor)
17. ServiceRequest      -> Patient, Encounter, Practitioner (requester)
18. MedicationRequest   -> Patient, Encounter, Medication (medicationReference), Practitioner
19. Immunization        -> Patient
20. CarePlan            -> Patient, Encounter, Condition (addresses)
21. DocumentReference   -> Patient, Encounter (context.encounter)
```

The C# uploader's `--mode individual` walks resource types in exactly this order.

## Notes and ordering caveats

- **Device → Patient (step 5 vs 7):** `Device.patient` references a Patient, which is created at
  step 7. In this dataset the two Devices reference `patient-001` and `patient-003`. If you upload
  strictly one-by-one, either move Device to **after** Patient, or upload it inside a transaction
  bundle (where order does not matter). The uploader keeps Device early to match the requested
  canonical order; on an RI-enforcing server, prefer the transaction bundles for Device, or reorder
  Device after Patient. This is the one place the "canonical" order and strict RI disagree.
- **CareTeam / Coverage before Encounter:** these reference Patient/Practitioner/Organization
  (all created earlier). CareTeam also references Encounter; on strict RI upload, move CareTeam to
  after Encounter, or use a transaction bundle. Again, bundles remove the ambiguity.

## Transaction bundles resolve references regardless of entry order

Inside a **single** FHIR transaction Bundle, the server resolves internal references (relative
`Type/id` for PUT-with-ids, or `urn:uuid` for POST) **before** committing, so entry order does not
matter. This is why the recommended path is:

- **HAPI / Aidbox:** upload `bundles/hapi-aidbox/foundation-bundle.json` first (shared
  Organizations, Locations, Practitioners, PractitionerRoles, Devices, Medications), then each
  `patient-00X-bundle.json`. The foundation-then-patients split is only needed because references
  cross bundle boundaries; within each bundle, order is irrelevant.
- **Medplum:** upload `bundles/medplum/medplum-all-bundle.json` — one transaction, all `urn:uuid`
  references resolve internally, server assigns ids. (Per-patient Medplum bundles are
  self-contained so they also resolve on their own.)

## Deletion order (reverse)

To tear down on an RI-enforcing server, delete in **reverse** dependency order (DocumentReference,
CarePlan, Immunization, MedicationRequest, … down to Organization), otherwise deleting a still-
referenced resource is blocked.
