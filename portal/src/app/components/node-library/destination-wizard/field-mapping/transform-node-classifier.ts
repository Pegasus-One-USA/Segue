import { TransformNodeType } from './transformation-rules.service';

/** Every node type, with a plain-language label — used both as the "show everything" fallback and to
 *  label whichever subset the classifier below picks out for a given field. */
export const ALL_NODE_TYPE_OPTIONS: { value: TransformNodeType; label: string }[] = [
  { value: 'DateTimeFormat', label: 'Date/Time Format' },
  { value: 'NumberCast', label: 'Number Cast' },
  { value: 'BooleanConversion', label: 'Boolean Conversion' },
  { value: 'UnitConversion', label: 'Unit Conversion (UCUM)' },
  { value: 'QuantityRangeAssembly', label: 'Quantity/Range Assembly' },
  { value: 'RoundingScaling', label: 'Rounding/Scaling' },
  { value: 'ValueCodeMapping', label: 'Value/Code Mapping' },
  { value: 'CodeableConceptBuilder', label: 'CodeableConcept Builder' },
  { value: 'StatusEnumCoercion', label: 'Status/Enum Coercion' },
  { value: 'ReferenceConstruction', label: 'Reference Construction' },
  { value: 'IdentifierFormatting', label: 'Identifier Formatting' },
  { value: 'HumanNameParsing', label: 'HumanName Parsing' },
  { value: 'AddressParsing', label: 'Address Parsing' },
  { value: 'TelecomNormalization', label: 'Telecom Normalization' },
  { value: 'StringNormalization', label: 'String Normalization' },
  { value: 'ConcatenationTemplating', label: 'Concatenation/Templating' },
  { value: 'ArrayListOperations', label: 'Array/List Operations' },
  { value: 'DefaultNullHandling', label: 'Default/Null Handling' },
  { value: 'DateMathAge', label: 'Date Math/Age' },
  { value: 'HashingMasking', label: 'Hashing/Masking' },
];

const LABEL_BY_TYPE = new Map(ALL_NODE_TYPE_OPTIONS.map(o => [o.value, o.label]));

// Nodes genuinely useful on almost any field, regardless of what kind of data it holds.
const UNIVERSAL: TransformNodeType[] = ['DefaultNullHandling', 'StringNormalization'];

/**
 * (fhirPath/valueType test) -> the node types relevant when it matches. Tested against the SOURCE field's
 * real FHIR path and coarse value type (from the FHIR catalog / MappingSourceRef), not the destination
 * column's name — the catalog's fhirPath is authoritative structure ("identifier.value", "subject.reference",
 * "name.given"), whereas a destination column name is just whatever the user happened to type.
 */
const CATEGORY_RULES: { test: (fhirPath: string, valueType?: string) => boolean; nodes: TransformNodeType[] }[] = [
  {
    test: (path, valueType) => valueType?.toLowerCase() === 'date' || valueType?.toLowerCase() === 'datetime' ||
      /date|dob|birth|onset|recorded|effective|period|death/i.test(path),
    nodes: ['DateTimeFormat', 'DateMathAge'],
  },
  {
    test: (_path, valueType) => valueType?.toLowerCase() === 'boolean',
    nodes: ['BooleanConversion'],
  },
  {
    test: (path, valueType) => valueType?.toLowerCase() === 'integer' || valueType?.toLowerCase() === 'decimal' ||
      /value|quantity|dosage|amount|count|score|weight|height/i.test(path),
    nodes: ['NumberCast', 'UnitConversion', 'QuantityRangeAssembly', 'RoundingScaling'],
  },
  {
    test: path => /identifier|^id$|\.id$|mrn|npi|ssn/i.test(path),
    nodes: ['IdentifierFormatting', 'HashingMasking'],
  },
  {
    test: path => /code(\.|$)|status|clinicalStatus|verificationStatus/i.test(path),
    nodes: ['ValueCodeMapping', 'CodeableConceptBuilder', 'StatusEnumCoercion'],
  },
  {
    test: path => /^name\.|\.name\.|name\.given|name\.family/i.test(path),
    nodes: ['HumanNameParsing', 'ConcatenationTemplating'],
  },
  {
    test: path => /address|\.city|\.state|\.postalCode|\.line/i.test(path),
    nodes: ['AddressParsing'],
  },
  {
    test: path => /telecom|phone|email|contact/i.test(path),
    nodes: ['TelecomNormalization', 'HashingMasking'],
  },
  {
    test: path => /\.reference$|subject\.reference|patient\.reference/i.test(path),
    nodes: ['ReferenceConstruction'],
  },
];

/**
 * Which of the 20 transform nodes make sense to offer for a given SOURCE field — driven by the field's real
 * FHIR path and coarse value type, not a guess against the destination column's arbitrary name. Falls back
 * to every node type if nothing matches (unknown/system field), rather than hiding one someone actually needs.
 */
export function getApplicableNodeTypes(
  sourceFhirPath: string | undefined | null, valueType?: string | null,
): { value: TransformNodeType; label: string }[] {
  const path = sourceFhirPath ?? '';
  const matched = new Set<TransformNodeType>(UNIVERSAL);
  for (const rule of CATEGORY_RULES) {
    if (rule.test(path, valueType ?? undefined)) {
      rule.nodes.forEach(n => matched.add(n));
    }
  }

  if (matched.size === UNIVERSAL.length) {
    return ALL_NODE_TYPE_OPTIONS;
  }

  return Array.from(matched).map(value => ({ value, label: LABEL_BY_TYPE.get(value) ?? value }));
}
