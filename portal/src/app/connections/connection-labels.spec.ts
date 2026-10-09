import { audienceLabel, databaseTypeLabel, destinationTypeLabel, ehrVendorLabel, ehrWriteBackLabel, writeVendorLabel } from './connection-labels';

describe('connection labels', () => {
  it('names EHR vendors the way admins do', () => {
    expect(ehrVendorLabel('Epic')).toBe('Epic');
    expect(ehrVendorLabel('Healow')).toBe('eClinicalWorks');
    expect(ehrVendorLabel('Athenahealth')).toBe('athenahealth');
    expect(ehrVendorLabel('GenericFhir')).toBe('Generic FHIR R4');
    expect(ehrVendorLabel('Cerner')).toBe('Cerner (Oracle Health)');
    expect(ehrVendorLabel('SomethingNew')).toBe('SomethingNew');
  });

  it('calls a Generic FHIR write connection a FHIR server', () => {
    expect(writeVendorLabel('GenericFhir')).toBe('FHIR test server');
    expect(writeVendorLabel('Healow')).toBe('eClinicalWorks');
    expect(writeVendorLabel('Cerner')).toBe('Cerner (Oracle Health)');
  });

  it('labels databases by engine', () => {
    expect(databaseTypeLabel('postgresql')).toBe('SQL database (PostgreSQL)');
    expect(databaseTypeLabel('sqlserver')).toBe('SQL database (SQL Server)');
    expect(databaseTypeLabel('mysql')).toBe('SQL database (MySQL)');
    expect(databaseTypeLabel('oracle')).toBe('SQL database (oracle)');
  });

  it('labels destinations from the catalog, with EHR write-back added', () => {
    expect(destinationTypeLabel('SqlServer')).toBe('SQL Server');
    expect(destinationTypeLabel('EhrWriteBack')).toBe('EHR write-back');
    expect(destinationTypeLabel('Unknown')).toBe('Unknown');
  });

  it('names an EHR write-back by its EHR as the tile does', () => {
    expect(ehrWriteBackLabel('Epic')).toBe('EHR write-back — Epic');
    expect(ehrWriteBackLabel('Healow')).toBe('EHR write-back — eClinicalWorks');
    expect(ehrWriteBackLabel('Athenahealth')).toBe('EHR write-back — athenahealth');
    expect(ehrWriteBackLabel('GenericFhir')).toBe('EHR write-back — FHIR test server');
    expect(ehrWriteBackLabel(null)).toBe('EHR write-back');
  });

  it('labels audiences, null when unknown or unset', () => {
    expect(audienceLabel('Backend')).toBe('Backend System');
    expect(audienceLabel('EhrLaunch')).toBe('Provider EHR Launch');
    expect(audienceLabel(null)).toBeNull();
    expect(audienceLabel('BackendServices')).toBeNull();
  });
});
