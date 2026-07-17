import { Observable } from 'rxjs';
import { SourceConnectionModel, SourceConnectionRequest } from '../models/source-connection.model';

export abstract class ISourceConnectionService {
  abstract getAll(): Observable<SourceConnectionModel[]>;
  abstract getById(id: string): Observable<SourceConnectionModel>;
  abstract create(req: SourceConnectionRequest): Observable<SourceConnectionModel>;
  abstract update(id: string, req: SourceConnectionRequest): Observable<SourceConnectionModel>;
  abstract delete(id: string): Observable<void>;
  /** Ids of every source connection currently referenced by at least one workflow's Source node. */
  abstract getUsedIds(): Observable<string[]>;
}
