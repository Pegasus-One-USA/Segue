# Vetted sample input per transform node type

Used to call `/api/v1/transformation-rules/preview`. These were tested against the real engine and
confirmed to succeed (no validation errors) — reuse them rather than inventing new ones, especially for
`TelecomNormalization` (real libphonenumber validation rejects the fictional `555-` exchange).

| Node type | Sample input | Notes |
|---|---|---|
| IdentifierFormatting | `"31415926"` | plain string |
| ReferenceConstruction | `"e3f1a2b0-patient-001"` | raw id; config's `resourceType: "Patient"` builds `Patient/e3f1a2b0-patient-001` |
| StatusEnumCoercion | `"active"` / any FHIR status code appropriate to the resource | falls back to `"unknown"` with the default blank `map: "{}"` config — that's expected, not a bug |
| DateTimeFormat | `"2026-03-14T09:30:00Z"` | |
| CodeableConceptBuilder | a real code in the configured system, e.g. `"2339-0"` (LOINC), `"44054006"` (SNOMED), `"860975"` (RxNorm), `"208"` (CVX), `"99213"`/`"45378"` (CPT) | resolves real display text from the local terminology DB when `resolveDisplayFromTerminology: true` |
| ValueCodeMapping | any short code string, e.g. `"male"`, `"active"` | |
| StringNormalization | messy text with irregular whitespace, e.g. `"  Type 2   Diabetes Mellitus  "` | |
| HumanNameParsing | `"Jane Marie Doe"` or `"Dr. Robert James Chen MD"` | correctly splits prefix/given/family/suffix |
| AddressParsing | `"742 Evergreen Terrace"` | |
| TelecomNormalization | a real-format US number **not** using the `555` exchange, e.g. `"(212) 501-5309"`, `"212-501-4488"` | `555` numbers are reserved-for-fiction and real phone validation rejects them |
| ConcatenationTemplating | `["Jane", "Doe"]` — **see the array-input limitation in SKILL.md step 7**, hand-compute instead of trusting the preview output | |
| ArrayListOperations | `["212-501-0142", "212-501-0199"]` — same array-input limitation | |
| DefaultNullHandling | `null` | demonstrates the default/data-absent-reason path |
| DateMathAge | a birthdate string, e.g. `"1985-04-12"` | |
| HashingMasking | `"123-45-6789"` | masks to `"*******6789"` with `keepLength: 4` |
| BooleanConversion | `"Y"` / `"N"` | |
| NumberCast | `"72.5"` | |
| UnitConversion | `"150"` | with default config (`lb_av → kg`), 150 → 68.04 |
| QuantityRangeAssembly | `"5.4"` | |
| RoundingScaling | `"98.6789"` | rounds to configured decimal places |
