import { buildResourceTree, buildForest, flattenLeaves, findNode } from './field-mapping-tree.util';
import { ResourceFieldDef } from '../destination-wizard.component';

describe('buildResourceTree', () => {
  it('nests catalog-sourced fields by dot-separated relative path', () => {
    const fields: ResourceFieldDef[] = [
      { label: 'Given', path: 'Patient.name.given', sqlColumn: 'Given', csvColumn: 'Given', jsonPath: '$.name[*].given[*]', valueType: 'String', arrays: ['name'] },
      { label: 'Family', path: 'Patient.name.family', sqlColumn: 'Family', csvColumn: 'Family', jsonPath: '$.name[*].family', valueType: 'String', arrays: ['name'] },
      { label: 'Patient ID', path: 'Patient.id', sqlColumn: 'SourcePatientId', csvColumn: 'PatientId', jsonPath: '$.id', valueType: 'String', arrays: [] },
    ];
    const tree = buildResourceTree('Patient', fields);

    expect(tree.kind).toBe('group');
    expect(tree.children.map(c => c.label).sort()).toEqual(['Name', 'Patient ID']);

    const nameGroup = tree.children.find(c => c.label === 'Name')!;
    expect(nameGroup.kind).toBe('group');
    expect(nameGroup.isArray).toBeTrue();
    expect(nameGroup.groupPath).toBe('name');
    expect(nameGroup.children.map(c => c.label)).toEqual(['Given', 'Family']);
    expect(nameGroup.children[0].kind).toBe('leaf');
    expect(nameGroup.children[0].field?.fhirPath).toBe('Patient.name.given');

    // Leaf labels come straight from the catalog's own label, not a derived path segment —
    // only group labels are synthesized via titleCase(segment).
    const idLeaf = tree.children.find(c => c.label === 'Patient ID')!;
    expect(idLeaf.kind).toBe('leaf');
  });

  it('handles a deeply nested array-of-array path (code.coding.code / code.coding.display)', () => {
    const fields: ResourceFieldDef[] = [
      { label: 'Code', path: 'Observation.code.coding.code', sqlColumn: 'Code', csvColumn: 'Code', jsonPath: '$.code.coding[*].code', valueType: 'String', arrays: ['code.coding'] },
      { label: 'Display', path: 'Observation.code.coding.display', sqlColumn: 'Display', csvColumn: 'Display', jsonPath: '$.code.coding[*].display', valueType: 'String', arrays: ['code.coding'] },
    ];
    const tree = buildResourceTree('Observation', fields);
    const codeGroup = tree.children[0];
    expect(codeGroup.label).toBe('Code');
    expect(codeGroup.isArray).toBeFalse(); // "code" itself is not the array — "code.coding" is
    const codingGroup = codeGroup.children[0];
    expect(codingGroup.label).toBe('Coding');
    expect(codingGroup.isArray).toBeTrue();
    expect(codingGroup.groupPath).toBe('code.coding');
    expect(codingGroup.children.map(c => c.label)).toEqual(['Code', 'Display']);
  });

  it('renders fallback (no jsonPath) fields as flat single-level leaves, no nesting', () => {
    const fields: ResourceFieldDef[] = [
      { label: 'First Name', path: 'Patient.name.given', sqlColumn: 'FirstName', csvColumn: 'FirstName' },
      { label: 'Last Name', path: 'Patient.name.family', sqlColumn: 'LastName', csvColumn: 'LastName' },
    ];
    const tree = buildResourceTree('Patient', fields);
    expect(tree.children.every(c => c.kind === 'leaf')).toBeTrue();
    expect(tree.children.map(c => c.label)).toEqual(['First Name', 'Last Name']);
  });

  it('preserves catalog order and returns an empty-children root for no fields', () => {
    const empty = buildResourceTree('Patient', []);
    expect(empty.children).toEqual([]);
  });
});

describe('buildForest', () => {
  it('builds one sibling root per resource, in selection order', () => {
    const availableFields = (r: string): ResourceFieldDef[] =>
      r === 'Patient'
        ? [{ label: 'Id', path: 'Patient.id', sqlColumn: 'Id', csvColumn: 'Id' }]
        : [{ label: 'Id', path: 'Encounter.id', sqlColumn: 'Id', csvColumn: 'Id' }];
    const forest = buildForest(['Encounter', 'Patient'], availableFields);
    expect(forest.map(r => r.resource)).toEqual(['Encounter', 'Patient']);
  });
});

describe('flattenLeaves', () => {
  it('collects every leaf under nested groups', () => {
    const fields: ResourceFieldDef[] = [
      { label: 'Given', path: 'Patient.name.given', sqlColumn: 'Given', csvColumn: 'Given', jsonPath: '$.name[*].given[*]', valueType: 'String', arrays: ['name'] },
      { label: 'Family', path: 'Patient.name.family', sqlColumn: 'Family', csvColumn: 'Family', jsonPath: '$.name[*].family', valueType: 'String', arrays: ['name'] },
    ];
    const tree = buildResourceTree('Patient', fields);
    expect(flattenLeaves(tree).map(l => l.label)).toEqual(['Given', 'Family']);
  });
});

describe('findNode', () => {
  it('finds a node anywhere in the forest by id', () => {
    const fields: ResourceFieldDef[] = [
      { label: 'Given', path: 'Patient.name.given', sqlColumn: 'Given', csvColumn: 'Given', jsonPath: '$.name[*].given[*]', valueType: 'String', arrays: ['name'] },
    ];
    const forest = buildForest(['Patient'], () => fields);
    expect(findNode(forest, 'Patient.name.given')?.label).toBe('Given');
    expect(findNode(forest, 'Patient.name')?.kind).toBe('group');
    expect(findNode(forest, 'nope')).toBeNull();
  });
});
