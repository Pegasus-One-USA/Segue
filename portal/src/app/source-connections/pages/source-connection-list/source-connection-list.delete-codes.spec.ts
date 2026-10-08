import { SourceConnectionListComponent } from './source-connection-list.component';
import { SourceConnectionModel } from '../../models/source-connection.model';

/**
 * A connection that can write is an EHR write connection too: deleting it from Source Connections needs the EHR
 * Write-Back delete right on top of the source-connection and vendor rights (the server checks all three).
 */
describe('SourceConnectionListComponent.deleteCodes', () => {
  const codesFor = (access: SourceConnectionModel['access']): string[] =>
    SourceConnectionListComponent.prototype.deleteCodes.call(
      {} as SourceConnectionListComponent,
      { sourceSystemType: 'Athenahealth', access } as SourceConnectionModel,
    );

  it('a read connection needs the source-connection and vendor delete rights', () => {
    expect(codesFor('Read')).toEqual(['sourceconnections.delete', 'athenahealth.delete']);
  });

  it('a Read & Write connection also needs ehrwriteback.delete', () => {
    expect(codesFor('ReadWrite')).toEqual(['sourceconnections.delete', 'athenahealth.delete', 'ehrwriteback.delete']);
  });
});
