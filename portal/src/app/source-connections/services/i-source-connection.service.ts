import { Observable } from 'rxjs';
import {
  GeneratedSigningKeyModel,
  PagedResult,
  SourceConnectionFilter,
  SourceConnectionModel,
  SourceConnectionRequest,
} from '../models/source-connection.model';

export abstract class ISourceConnectionService {
  abstract getAll(): Observable<SourceConnectionModel[]>;
  abstract getPaged(filter: SourceConnectionFilter, silent?: boolean): Observable<PagedResult<SourceConnectionModel>>;
  abstract getById(id: string): Observable<SourceConnectionModel>;
  abstract create(req: SourceConnectionRequest): Observable<SourceConnectionModel>;
  abstract update(id: string, req: SourceConnectionRequest): Observable<SourceConnectionModel>;
  abstract delete(id: string): Observable<void>;
  /** Ids of every source connection currently referenced by at least one workflow's Source node. */
  abstract getUsedIds(): Observable<string[]>;
  /** Generates a new SMART Backend Services (private_key_jwt) key pair server-side and stores the private key —
   *  not scoped to an existing connection, since the wizard calls this before a connection is saved. */
  abstract generateSigningKey(): Observable<GeneratedSigningKeyModel>;
  /** Validates and stores a customer-supplied RSA private key (PEM text read client-side from an uploaded file)
   *  for SMART Backend Services — the alternative to generateSigningKey() for a customer who already has an Epic
   *  app registered against their own key. Rejects (400) anything that isn't a real, unencrypted RSA private key. */
  abstract importSigningKey(privateKeyPem: string): Observable<GeneratedSigningKeyModel>;
  /** The public half of a SAVED connection's signing key, as a PEM file — what an EHR app registration takes when
   *  it accepts an uploaded public key instead of a JWKS URL. Only meaningful once the connection has an id. */
  abstract downloadPublicKeyPem(sourceConnectionId: string): Observable<Blob>;
  /** The private key PEM of a SAVED connection's signing key. Gated server-side by its own
   *  `sourceconnections.export` permission and written to the security event log on every download. */
  abstract downloadPrivateKeyPem(sourceConnectionId: string): Observable<Blob>;
}
