import {
  FieldLineageChain,
  FieldLineageFilter,
  FieldLineageHop,
  LineageSummary,
  PagedResult,
  ResourceTypeSummary,
} from '../../models/execution-history.model';

// Fallback content for runs that genuinely recorded zero field-lineage rows (e.g. 0 node runs) — rather
// than the panel just going empty. Every stat/tree count below is DERIVED from the same chain list the
// table renders (see buildMockResourceTree/buildMockLineageSummary), so nothing here can drift out of sync
// with what a user actually sees when they expand a resource type or pick a field.

type MockResourceType = 'Patient' | 'Observation' | 'Encounter';

const RESOURCE_IDS: Record<MockResourceType, string[]> = {
  Patient: [
    '1f2e6b8a-9c3d-4a51-8e77-2b0f6d4c9a11',
    '7ad3c9e2-5f14-4b8a-9d02-3e6c1a7f8b44',
    'c48e1d76-2a93-4f0c-b5e8-9d7a3c2f1e60',
    '5e91b3c7-4a06-4d82-9f38-1b7e6c9a2d05',
  ],
  Observation: [
    'a9b71e2c-6d4f-4c88-9a13-5e0f7b2d8c34',
    'e35f0a9d-8b21-4d67-9f52-1c4a6e8b3d70',
    '2d6c9a4e-1f38-4b95-8c07-7a3e5d9c1b42',
    '8f4c1e6a-3b95-4d70-9e21-6a8c4f1b7d33',
  ],
  Encounter: [
    '6b1e4c9a-3d7f-4a82-9c15-8e2f0d6a4b93',
    'd94a2f7e-5c1b-4e63-8a09-3f6d1c8e2b57',
    '0c5e8a2d-9f41-4c76-8d23-6b1a4e9c7f30',
    '4a7d2c9e-8f16-4b53-9c04-2e7a5d1f8b64',
  ],
};

interface MockSample {
  source: string;
  /** null models a hop that failed — errorMessage explains why, and no second hop follows it. */
  dest: string | null;
  errorMessage?: string;
}

interface MockFieldTemplate {
  destinationField: string;
  sourceField: string;
  nodeType: string;
  configJson: string;
  /** One sample per RESOURCE_IDS[type] slot, same order/length. */
  samples: MockSample[];
  /** Adds a trailing no-op StringNormalization hop, to show a real >1-node chain for a couple of fields. */
  secondHop?: boolean;
}

const FIELD_TEMPLATES: Record<MockResourceType, MockFieldTemplate[]> = {
  Patient: [
    {
      destinationField: 'name', sourceField: 'name.given/name.family', nodeType: 'HumanNameParsing',
      configJson: '{"order":"given family"}', secondHop: true,
      samples: [
        { source: '{"given":["Maria"],"family":"Alvarez"}', dest: '"Maria Alvarez"' },
        { source: '{"given":["James"],"family":"Whitfield"}', dest: '"James Whitfield"' },
        { source: '{"given":["Priya"],"family":"Natarajan"}', dest: '"Priya Natarajan"' },
        { source: '{"given":["Daniel"],"family":"Osei"}', dest: '"Daniel Osei"' },
      ],
    },
    {
      destinationField: 'birthDate', sourceField: 'birthDate', nodeType: 'DateTimeFormat',
      configJson: '{"format":"MM/dd/yyyy"}',
      samples: [
        { source: '"1988-04-12"', dest: '"04/12/1988"' },
        { source: '"1975-11-02"', dest: '"11/02/1975"' },
        { source: '"1990-06-23"', dest: '"06/23/1990"' },
        { source: '"2002-01-30"', dest: '"01/30/2002"' },
      ],
    },
    {
      destinationField: 'gender', sourceField: 'gender', nodeType: 'StatusEnumCoercion',
      configJson: '{"map":"AdministrativeGender to internal"}',
      samples: [
        { source: '"female"', dest: '"Female"' },
        { source: '"male"', dest: '"Male"' },
        { source: '"other"', dest: '"Other"' },
        { source: '"female"', dest: '"Female"' },
      ],
    },
    {
      destinationField: 'identifier', sourceField: 'identifier.value', nodeType: 'IdentifierFormatting',
      configJson: '{"system":"MRN"}',
      samples: [
        { source: '"00019284"', dest: '"MRN-00019284"' },
        { source: '"00028471"', dest: '"MRN-00028471"' },
        { source: '"00093310"', dest: null, errorMessage: 'MRN checksum failed validation' },
        { source: '"00047625"', dest: '"MRN-00047625"' },
      ],
    },
    {
      destinationField: 'address.line', sourceField: 'address.line', nodeType: 'AddressParsing',
      configJson: '{"component":"line[0]"}', secondHop: true,
      samples: [
        { source: '{"line":["482 Fenwick Ave"],"city":"Springfield"}', dest: '"482 Fenwick Ave"' },
        { source: '{"line":["19 Birchwood Ct"],"city":"Dover"}', dest: '"19 Birchwood Ct"' },
        { source: '{"line":["771 Meridian Blvd"],"city":"Ashland"}', dest: '"771 Meridian Blvd"' },
        { source: '{"line":["305 Willowmere Dr"],"city":"Bellview"}', dest: '"305 Willowmere Dr"' },
      ],
    },
  ],
  Observation: [
    {
      destinationField: 'valueQuantity', sourceField: 'valueQuantity', nodeType: 'UnitConversion',
      configJson: '{"targetUnit":"mg/dL"}',
      samples: [
        { source: '{"value":126,"unit":"mg/dL"}', dest: '"126 mg/dL"' },
        { source: '{"value":98,"unit":"mg/dL"}', dest: '"98 mg/dL"' },
        { source: '{"value":142,"unit":"mg/dL"}', dest: '"142 mg/dL"' },
        { source: '{"value":110,"unit":"mg/dL"}', dest: '"110 mg/dL"' },
      ],
    },
    {
      destinationField: 'effectiveDateTime', sourceField: 'effectiveDateTime', nodeType: 'DateTimeFormat',
      configJson: '{"format":"yyyy-MM-dd HH:mm"}',
      samples: [
        { source: '"2026-07-29T09:12:00Z"', dest: '"2026-07-29 09:12"' },
        { source: '"2026-07-29T10:47:00Z"', dest: '"2026-07-29 10:47"' },
        { source: '"2026-07-29T13:05:00Z"', dest: '"2026-07-29 13:05"' },
        { source: '"2026-07-29T14:22:00Z"', dest: '"2026-07-29 14:22"' },
      ],
    },
    {
      destinationField: 'code.coding.code', sourceField: 'code.coding.code', nodeType: 'ValueCodeMapping',
      configJson: '{"system":"LOINC"}',
      samples: [
        { source: '"2345-7"', dest: '"GLUCOSE_FASTING"' },
        { source: '"4548-4"', dest: '"HBA1C"' },
        { source: '"2160-0"', dest: '"CREATININE"' },
        { source: '"718-7"', dest: '"HEMOGLOBIN"' },
      ],
    },
    {
      destinationField: 'status', sourceField: 'status', nodeType: 'StatusEnumCoercion',
      configJson: '{"map":"ObservationStatus to internal"}',
      samples: [
        { source: '"final"', dest: '"Final"' },
        { source: '"final"', dest: '"Final"' },
        { source: '"preliminary"', dest: '"Preliminary"' },
        { source: '"unknown"', dest: null, errorMessage: 'Unrecognized status code "unknown"' },
      ],
    },
  ],
  Encounter: [
    {
      destinationField: 'status', sourceField: 'status', nodeType: 'StatusEnumCoercion',
      configJson: '{"map":"EncounterStatus to internal"}',
      samples: [
        { source: '"finished"', dest: '"Completed"' },
        { source: '"finished"', dest: '"Completed"' },
        { source: '"in-progress"', dest: '"InProgress"' },
        { source: '"finished"', dest: '"Completed"' },
      ],
    },
    {
      destinationField: 'period.start', sourceField: 'period.start', nodeType: 'DateTimeFormat',
      configJson: '{"format":"MM/dd/yyyy hh:mm a"}',
      samples: [
        { source: '"2026-07-29T08:00:00Z"', dest: '"07/29/2026 08:00 AM"' },
        { source: '"2026-07-29T11:30:00Z"', dest: '"07/29/2026 11:30 AM"' },
        { source: '"2026-07-29T14:15:00Z"', dest: '"07/29/2026 02:15 PM"' },
        { source: '"2026-07-29T16:40:00Z"', dest: '"07/29/2026 04:40 PM"' },
      ],
    },
    {
      destinationField: 'class.code', sourceField: 'class.code', nodeType: 'ValueCodeMapping',
      configJson: '{"system":"ActEncounterCode"}',
      samples: [
        { source: '"AMB"', dest: '"Ambulatory"' },
        { source: '"EMER"', dest: '"Emergency"' },
        { source: '"IMP"', dest: '"Inpatient"' },
        { source: '"AMB"', dest: '"Ambulatory"' },
      ],
    },
  ],
};

const SOURCE_SYSTEM_TYPE = 'Epic';
const SOURCE_CONNECTION_NAME = 'Epic Sandbox - Backend';
const DESTINATION_TYPE_NAME = 'SqlServer';
const DESTINATION_NAME = 'Analytics Warehouse';

/** Builds the fixed chain list once per panel instance. `nowMs` (pass `Date.now()`) only spaces out the
 *  hop timestamps so they look recent — the values/fields/failures themselves are fixed. */
export function buildMockLineageChains(nowMs: number): FieldLineageChain[] {
  const chains: FieldLineageChain[] = [];
  (Object.keys(FIELD_TEMPLATES) as MockResourceType[]).forEach(resourceType => {
    const ids = RESOURCE_IDS[resourceType];
    FIELD_TEMPLATES[resourceType].forEach(template => {
      template.samples.forEach((sample, i) => {
        const failed = sample.dest === null;
        const hops: FieldLineageHop[] = [{
          nodeOrder: 0,
          nodeType: template.nodeType,
          configJson: template.configJson,
          sourceValueJson: sample.source,
          destinationValueJson: sample.dest,
          success: !failed,
          errorMessage: failed ? (sample.errorMessage ?? 'Transform failed') : null,
          durationMs: 2 + (i % 4),
          executedAtUtc: new Date(nowMs - (chains.length + 1) * 45_000).toISOString(),
        }];
        if (template.secondHop && !failed) {
          hops.push({
            nodeOrder: 1,
            nodeType: 'StringNormalization',
            configJson: '{"trim":true}',
            sourceValueJson: sample.dest,
            destinationValueJson: sample.dest,
            success: true,
            errorMessage: null,
            durationMs: 1,
            executedAtUtc: new Date(nowMs - chains.length * 45_000).toISOString(),
          });
        }
        chains.push({
          resourceType,
          resourceId: ids[i % ids.length],
          destinationField: template.destinationField,
          sourceField: template.sourceField,
          hops,
          sourceSystemType: SOURCE_SYSTEM_TYPE,
          sourceConnectionName: SOURCE_CONNECTION_NAME,
          destinationTypeName: DESTINATION_TYPE_NAME,
          destinationName: DESTINATION_NAME,
        });
      });
    });
  });
  return chains;
}

/** Backs the resource-tree sidebar — resourceCount/field counts are tallied straight from `chains`. */
export function buildMockResourceTree(chains: FieldLineageChain[]): ResourceTypeSummary[] {
  const fieldCountsByType = new Map<string, Map<string, number>>();
  const resourceIdsByType = new Map<string, Set<string>>();

  for (const chain of chains) {
    if (!fieldCountsByType.has(chain.resourceType)) {
      fieldCountsByType.set(chain.resourceType, new Map());
      resourceIdsByType.set(chain.resourceType, new Set());
    }
    const fieldCounts = fieldCountsByType.get(chain.resourceType)!;
    fieldCounts.set(chain.destinationField, (fieldCounts.get(chain.destinationField) ?? 0) + 1);
    resourceIdsByType.get(chain.resourceType)!.add(chain.resourceId);
  }

  return Array.from(fieldCountsByType.entries()).map(([resourceType, fieldCounts]) => ({
    resourceType,
    resourceCount: resourceIdsByType.get(resourceType)!.size,
    fields: Array.from(fieldCounts.entries()).map(([destinationField, resourceCount]) => ({ destinationField, resourceCount })),
  }));
}

/** Backs the stat strip — every number is a rollup of `chains`, so it can never disagree with the table. */
export function buildMockLineageSummary(chains: FieldLineageChain[]): LineageSummary {
  const resourcesProcessed = new Set(chains.map(c => `${c.resourceType}/${c.resourceId}`)).size;
  const fieldsTransformed = chains.length;
  const transformationNodesExecuted = chains.reduce((sum, c) => sum + c.hops.length, 0);
  const successCount = chains.filter(c => c.hops.every(h => h.success)).length;
  return {
    resourcesProcessed,
    fieldsTransformed,
    transformationNodesExecuted,
    successRate: chains.length === 0 ? 1 : successCount / chains.length,
  };
}

/** Applies the same filter/paginate contract as the real fieldLineage endpoint, client-side, over `chains`. */
export function queryMockChains(
  chains: FieldLineageChain[],
  filter: FieldLineageFilter,
  page: number,
  pageSize: number,
): PagedResult<FieldLineageChain> {
  let items = chains;
  if (filter.resourceType) items = items.filter(c => c.resourceType === filter.resourceType);
  if (filter.destinationField) items = items.filter(c => c.destinationField === filter.destinationField);
  if (filter.resourceId) {
    const needle = filter.resourceId.toLowerCase();
    items = items.filter(c => c.resourceId.toLowerCase().includes(needle));
  }
  if (filter.nodeType) {
    const needle = filter.nodeType.toLowerCase();
    items = items.filter(c => c.hops.some(h => h.nodeType.toLowerCase().includes(needle)));
  }
  if (filter.search) {
    const needle = filter.search.toLowerCase();
    items = items.filter(c => c.destinationField.toLowerCase().includes(needle) || c.resourceType.toLowerCase().includes(needle));
  }

  const totalCount = items.length;
  const start = (page - 1) * pageSize;
  return { items: items.slice(start, start + pageSize), totalCount, page, pageSize };
}
