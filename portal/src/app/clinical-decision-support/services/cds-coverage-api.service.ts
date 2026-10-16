import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams, HttpResponse } from '@angular/common/http';
import { Observable, catchError, from, map, of, switchMap, throwError } from 'rxjs';
import { CDS_ENDPOINTS } from '../../core/api-endpoints';
import { extractBackendMessage } from '../../core/http-error-sanitizer.interceptor';
import {
  CdsCoverageFileFormat,
  CdsCoverageImportRequest,
  CdsCoverageImportResult,
  CdsCoverageRule,
  CdsCoverageRuleFilter,
  CdsCoverageRulePage,
  CdsDownloadedFile,
  CdsPayer,
  CdsPayerRelayTestResult,
  SaveCdsCoverageRuleRequest,
  SaveCdsPayerRequest,
} from '../models/cds-coverage.models';

/** The file name from a Content-Disposition header, else `fallback`. */
export function cdsFileNameFrom(response: HttpResponse<Blob>, fallback: string): string {
  const header = response.headers.get('Content-Disposition');
  const star = header?.match(/filename\*=(?:UTF-8'')?([^;]+)/i);
  if (star?.[1]) return decodeURIComponent(star[1].trim().replace(/^"|"$/g, ''));
  const plain = header?.match(/filename="?([^";]+)"?/i);
  return plain?.[1]?.trim() || fallback;
}

/** Saves a downloaded file in the browser. */
export function cdsSaveFile(file: CdsDownloadedFile): void {
  const url = URL.createObjectURL(file.blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = file.fileName;
  anchor.click();
  URL.revokeObjectURL(url);
}

/**
 * Coverage requirements (Da Vinci CRD) admin API: payers, coverage rules, rule file import / export and the payer
 * relay test. Every call needs a cdshooks.* permission; errors surface the backend's own message.
 */
@Injectable({ providedIn: 'root' })
export class CdsCoverageApiService {
  private readonly http = inject(HttpClient);

  // ─── Payers ──────────────────────────────────────────────────────────────────

  getPayers(): Observable<CdsPayer[]> {
    return this.http.get<CdsPayer[]>(CDS_ENDPOINTS.payers);
  }

  createPayer(request: SaveCdsPayerRequest): Observable<CdsPayer> {
    return this.http.post<CdsPayer>(CDS_ENDPOINTS.payers, request);
  }

  updatePayer(id: string, request: SaveCdsPayerRequest): Observable<CdsPayer> {
    return this.http.put<CdsPayer>(CDS_ENDPOINTS.payerById(id), request);
  }

  /** Deletes the payer and all of its rules. */
  deletePayer(id: string): Observable<void> {
    return this.http.delete<void>(CDS_ENDPOINTS.payerById(id));
  }

  /** Sends a made-up order (no patient data) to the payer's CRD service, signed as Segue. */
  testRelay(id: string): Observable<CdsPayerRelayTestResult> {
    return this.http.post<CdsPayerRelayTestResult>(CDS_ENDPOINTS.testRelay(id), null);
  }

  // ─── Rules ───────────────────────────────────────────────────────────────────

  getRules(filter: CdsCoverageRuleFilter): Observable<CdsCoverageRulePage> {
    let params = new HttpParams().set('page', String(filter.page)).set('pageSize', String(filter.pageSize));
    if (filter.payerId) params = params.set('payerId', filter.payerId);
    if (filter.code) params = params.set('code', filter.code);
    if (filter.search) params = params.set('search', filter.search);
    if (filter.enabled !== undefined) params = params.set('enabled', String(filter.enabled));
    return this.http.get<CdsCoverageRulePage>(CDS_ENDPOINTS.coverageRules, { params });
  }

  createRule(request: SaveCdsCoverageRuleRequest): Observable<CdsCoverageRule> {
    return this.http.post<CdsCoverageRule>(CDS_ENDPOINTS.coverageRules, request);
  }

  updateRule(id: string, request: SaveCdsCoverageRuleRequest): Observable<CdsCoverageRule> {
    return this.http.put<CdsCoverageRule>(CDS_ENDPOINTS.coverageRuleById(id), request);
  }

  deleteRule(id: string): Observable<void> {
    return this.http.delete<void>(CDS_ENDPOINTS.coverageRuleById(id));
  }

  // ─── Files ───────────────────────────────────────────────────────────────────

  /** Checks (mode preview: writes nothing) or applies (mode apply: every row or none) a rule file. */
  importRules(request: CdsCoverageImportRequest): Observable<CdsCoverageImportResult> {
    let params = new HttpParams()
      .set('format', request.format)
      .set('mode', request.mode)
      .set('strategy', request.strategy);
    if (request.payerId) params = params.set('payerId', request.payerId);
    const body = new FormData();
    body.append('file', request.file, request.file.name);
    return this.http.post<CdsCoverageImportResult>(CDS_ENDPOINTS.coverageRulesImport, body, { params });
  }

  /** The rules (all payers, or one) in the import format. */
  exportRules(format: CdsCoverageFileFormat, payerId: string | null): Observable<CdsDownloadedFile> {
    let params = new HttpParams().set('format', format);
    if (payerId) params = params.set('payerId', payerId);
    return this.download(CDS_ENDPOINTS.coverageRulesExport, params, `segue-coverage-rules.${format}`);
  }

  /** The import template: the header and two made-up rows. */
  getImportTemplate(format: CdsCoverageFileFormat): Observable<CdsDownloadedFile> {
    return this.download(
      CDS_ENDPOINTS.coverageRulesTemplate, new HttpParams().set('format', format), `segue-coverage-rules-template.${format}`);
  }

  private download(url: string, params: HttpParams, fallbackName: string): Observable<CdsDownloadedFile> {
    return this.http.get(url, { params, observe: 'response', responseType: 'blob' }).pipe(
      map(response => ({ fileName: cdsFileNameFrom(response, fallbackName), blob: response.body ?? new Blob([]) })),
      catchError((err: unknown) => cdsBlobErrorWithMessage(err)),
    );
  }
}

/**
 * A blob download's error body arrives as a Blob, so the error interceptor cannot read the API's reason from it and
 * `err.message` is only a generic status text. This reads the Blob as JSON and, when it holds a safe backend
 * message, rethrows the error with that message (and the parsed body as `error`). Anything else is rethrown as it is.
 */
export function cdsBlobErrorWithMessage(err: unknown): Observable<never> {
  if (!(err instanceof HttpErrorResponse) || !(err.error instanceof Blob)) return throwError(() => err);
  return from(err.error.text()).pipe(
    catchError(() => of('')),
    switchMap(text => {
      let body: unknown = null;
      try {
        body = text ? JSON.parse(text) : null;
      } catch {
        body = null;
      }
      const message = extractBackendMessage(body);
      if (!message) return throwError(() => err);
      const rebuilt = new HttpErrorResponse({
        error: body, headers: err.headers, status: err.status, statusText: err.statusText, url: err.url ?? undefined,
      });
      // Same as the error interceptor: Angular computes `message` itself, so it is overwritten with the safe text.
      (rebuilt as unknown as { message: string }).message = message;
      return throwError(() => rebuilt);
    }),
  );
}
