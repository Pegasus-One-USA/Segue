import {
  creatableWriteVendorCards,
  sourceConnectionDeleteCodes,
  sourceConnectionVendorEditCode,
  toConnectionKindCard,
  writeConnectionCreateCodes,
} from './connection-permissions';
import { SourceConnectionModel } from '../source-connections/models/source-connection.model';

/**
 * A connection that can write is an EHR write connection too: deleting it needs the EHR Write-Back delete right on
 * top of the source-connection and vendor rights (the server checks all three).
 */
describe('sourceConnectionDeleteCodes', () => {
  const codesFor = (access: SourceConnectionModel['access']) =>
    sourceConnectionDeleteCodes({ sourceSystemType: 'Athenahealth', access });

  it('a read connection needs the source-connection and vendor delete rights', () => {
    expect(codesFor('Read')).toEqual(['sourceconnections.delete', 'athenahealth.delete']);
  });

  it('a Read & Write connection also needs ehrwriteback.delete', () => {
    expect(codesFor('ReadWrite')).toEqual(['sourceconnections.delete', 'athenahealth.delete', 'ehrwriteback.delete']);
  });

  it('a write-only connection also needs ehrwriteback.delete', () => {
    expect(codesFor('Write')).toEqual(['sourceconnections.delete', 'athenahealth.delete', 'ehrwriteback.delete']);
  });

  it('edit is the vendor right', () => {
    expect(sourceConnectionVendorEditCode({ sourceSystemType: 'Epic' })).toBe('epic.edit');
  });
});

/** Creating a write connection needs ehrwriteback.create and the vendor's own create right, wherever it is offered. */
describe('creatableWriteVendorCards', () => {
  const granting = (...codes: string[]) => (code: string) => codes.includes(code);

  it('needs both rights, using each vendor\'s own permission prefix', () => {
    expect(writeConnectionCreateCodes('Healow')).toEqual(['ehrwriteback.create', 'healow.create']);
    expect(writeConnectionCreateCodes('Athenahealth')).toEqual(['ehrwriteback.create', 'athenahealth.create']);
    expect(writeConnectionCreateCodes('GenericFhir')).toEqual(['ehrwriteback.create', 'genericfhir.create']);
  });

  it('offers the vendors the role holds both rights for, in card order', () => {
    const cards = creatableWriteVendorCards(granting('ehrwriteback.create', 'epic.create', 'genericfhir.create'));
    expect(cards.map(c => c.value)).toEqual(['Epic', 'GenericFhir']);
  });

  it('offers nothing without ehrwriteback.create', () => {
    expect(creatableWriteVendorCards(granting('epic.create'))).toEqual([]);
  });

  it('narrows to one vendor when asked', () => {
    const has = granting('ehrwriteback.create', 'epic.create', 'genericfhir.create');
    expect(creatableWriteVendorCards(has, 'GenericFhir').map(c => c.value)).toEqual(['GenericFhir']);
    expect(creatableWriteVendorCards(has, 'Healow')).toEqual([]);
  });

  it('turns a vendor into a picker card', () => {
    const [epic] = creatableWriteVendorCards(granting('ehrwriteback.create', 'epic.create'));
    expect(toConnectionKindCard(epic)).toEqual(
      { kind: 'ehr-write', value: 'Epic', label: 'Epic', sub: epic.sub, abbr: 'EP', color: epic.color });
  });
});
