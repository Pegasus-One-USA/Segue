# The 37 resource types and their rule coverage

Source: the Segue Epic sandbox `SourceConnection`'s SMART Backend Services scope list
(`system/<Type>.rs` per type). Used as the canonical "all resource types this connector can pull" list.

```
Patient, Practitioner, Encounter, AllergyIntolerance, Observation, Condition, Procedure,
ServiceRequest, DiagnosticReport, MedicationRequest, MedicationAdministration, Appointment,
Binary, CarePlan, CareTeam, Communication, CommunicationRequest, Device, DocumentReference,
FamilyMemberHistory, Goal, ImagingStudy, Immunization, Location, Medication, MedicationDispense,
MedicationStatement, Organization, PractitionerRole, Provenance, Questionnaire,
QuestionnaireResponse, RelatedPerson, Schedule, Slot, Specimen, Task
```

## Coverage status (as of the session that authored this skill)

- **4 resources — Field scope, live in the reference workflow's actual SQL mapping:**
  Patient (10 rules), Observation (7 rules), Encounter (2 rules), Condition (1 rule) = 20 rules.
- **32 resources — ResourceType scope, workflow-wide defaults, not yet mapped to a destination column
  in any specific workflow but resolve automatically the moment one is added:** 174 rules total. See the
  published artifact from that session for the full resource → rule-type breakdown (ask the user for the
  link, or re-derive from `GET /api/v1/transformation-rules?scope=ResourceType`).
- **1 resource — no rule applies:** Binary. It's an opaque base64 attachment + contentType with no coded,
  textual, identifier, date, or quantity field for any of the 20 catalog rule types to act on. This is
  correct, not a gap.

## The 20-rule catalog (`TransformNodeType` enum)

```
DateTimeFormat, NumberCast, BooleanConversion, UnitConversion, QuantityRangeAssembly, RoundingScaling,
ValueCodeMapping, CodeableConceptBuilder, StatusEnumCoercion, ReferenceConstruction, IdentifierFormatting,
HumanNameParsing, AddressParsing, TelecomNormalization, StringNormalization, ConcatenationTemplating,
ArrayListOperations, DefaultNullHandling, DateMathAge, HashingMasking
```

This is a fixed, closed set (see the doc comment on `TransformNodeType`: "The 20 field-level FHIR-aware
transform nodes (FHIRBridge_Top20_Transformations.pdf v1.0)"). Don't invent a 21st — if a new
transformation concept is needed, it's a product decision, not something this skill should improvise.
