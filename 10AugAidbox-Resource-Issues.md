# `10AugAidbox` — Resource Issues

Workflow run analyzed: `38C4BBB4-F15E-40FD-8E90-FD10E3AD5F5C` (Failed), started 2026-08-10 12:14:45 UTC. Correlation ID: `0HNNMQ6VSQKEE:0000001A`.

This run has only one recorded node execution — the Epic source node (`Epic-2`) — which failed outright while extracting **Practitioner**, aborting the whole run before the Aidbox destination node ever ran. Every other configured resource type (Encounter, Observation, Condition, Procedure, ServiceRequest, DiagnosticReport, MedicationRequest, MedicationAdministration, AllergyIntolerance) was never attempted in this run, so their status is unknown, not confirmed-working.

| Resource Type | Status | Description |
|---|---|---|
| **Practitioner** | ❌ Failed (blocks entire run) | Epic returned `400 Bad Request`: *"Either name, family, or identifier is a required parameter."* The source node's `Search criteria` field is set to `identifier=203710,203715,203712,203709` — Patient identifiers — and that same criteria string is applied uniformly to every configured resource type. It satisfies Patient's search requirements but not Practitioner's: Epic's `Practitioner.Search` requires a `name`, `family`, or `identifier` value that is actually a *Practitioner* identifier, not a Patient one. Practitioner needs its own search strategy (e.g. resolved via each Patient's own `generalPractitioner` reference, or a Practitioner-specific identifier list) rather than reusing the Patient search criteria as-is. |
| Patient | ✅ Not implicated | No failure recorded against Patient in this run; extraction reached Practitioner (the 2nd configured resource type), implying Patient itself completed without error. |
| Encounter, Observation, Condition, Procedure, ServiceRequest, DiagnosticReport, MedicationRequest, MedicationAdministration, AllergyIntolerance | ⚪ Not reached | Extraction is sequential per resource type; the run aborted at Practitioner before any of these were attempted. Re-running after the Practitioner fix is needed to know their real status. |

## Root cause

Source node config (`Epic-2`):
```
Search criteria: identifier=203710,203715,203712,203709
Resources:       Patient, Practitioner, Encounter, AllergyIntolerance, Observation,
                 Condition, Procedure, ServiceRequest, DiagnosticReport,
                 MedicationRequest, MedicationAdministration
```

One `Search criteria` value is being reused verbatim across every resource type in `Resources`, but that value is only valid for Patient's own identifier search. Practitioner (and possibly other non-Patient-identifier-keyed resource types once reached) needs resource-type-aware search parameters, not a single shared criteria string.

## Suggested fix
Configure Practitioner's extraction to search by its own valid parameter — either drop Practitioner from this workflow's `Resources` list if it isn't actually needed standalone (it will still arrive embedded as a reference on Patient/Encounter/etc.), or give it a Practitioner-appropriate search criteria (e.g. resolved from each fetched Patient's `generalPractitioner` reference) instead of reusing the Patient identifier filter.
