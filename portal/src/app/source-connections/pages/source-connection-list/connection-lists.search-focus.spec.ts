import { Type } from '@angular/core';
import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { SourceConnectionListComponent } from './source-connection-list.component';
import { DestinationConnectionListComponent } from '../../../destination-connections/pages/destination-connection-list/destination-connection-list.component';
import { SKIP_LOADER } from '../../../core/loading.interceptor';
import { AuthStore } from '../../../auth/store/auth.store';
import { ISourceConnectionService } from '../../services/i-source-connection.service';
import { ApiSourceConnectionService } from '../../services/api-source-connection.service';

/**
 * Typing in the Source / Destination Connections search lost focus after every keystroke: a request without
 * SKIP_LOADER raises the app-wide loader, which makes the page inert and takes focus out of the box. Search now runs
 * in the browser over rows already loaded, so a keystroke sends no list request at all — and anything a keystroke
 * does cause must still skip the loader.
 */
const SOURCE_ROW = {
  id: 's1', name: 'ECW', sourceSystemType: 'Healow', applicationType: 'Backend', baseUrl: 'https://x',
  authentication: { authenticationType: 'None', clientId: 'c', scopes: [] }, isEnabled: true, access: 'ReadWrite',
};
const DATABASE_ROW = { id: 'db1', name: 'Clinic DB', engine: 'postgresql', secretKeyVaultName: 'v', secretName: 's', createdBy: null, updatedOnUtc: null };
const DESTINATION_ROW = { id: 'd1', name: 'Medplum 1', destinationType: 'Medplum', keyVaultName: 'v', secretName: 's', target: null, isEnabled: true };

function respond(r: TestRequest): void {
  const url = r.request.url;
  if (url.includes('usage')) r.flush([]);
  else if (url.includes('has-execution-history')) r.flush({ hasExecutionHistory: false });
  else if (url.includes('sql-connections')) r.flush([DATABASE_ROW]);
  else if (url.includes('source-connections')) r.flush([SOURCE_ROW]);
  else if (url.includes('destinations/paged')) r.flush({ items: [DESTINATION_ROW], totalCount: 1, page: 1, pageSize: 100 });
  else r.flush([DESTINATION_ROW]);
}

const LIST_URLS = ['source-connections', 'sql-connections', 'destinations'];
const isListRequest = (r: TestRequest) =>
  !r.request.url.includes('usage') && !r.request.url.includes('has-execution-history')
  && LIST_URLS.some(part => r.request.url.includes(part));

function suite(label: string, list: Type<unknown>, prefix: string): void {
  describe(label, () => {
    it('sends no list request on a search keystroke, and nothing it does send raises the app-wide loader', fakeAsync(() => {
      TestBed.configureTestingModule({
        imports: [list],
        providers: [
          provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
          { provide: ISourceConnectionService, useClass: ApiSourceConnectionService },
          { provide: AuthStore, useValue: { isAuthenticated: () => true, user: () => null, isAdmin: () => true, hasPermission: () => true } },
        ],
      });
      const http = TestBed.inject(HttpTestingController);
      const fixture = TestBed.createComponent(list);
      fixture.detectChanges();
      tick(1000);
      // Answer the initial load (and anything else on open), then the follow-ups it causes.
      http.match(() => true).forEach(respond);
      fixture.detectChanges();
      tick(1000);
      http.match(() => true).forEach(respond);
      fixture.detectChanges();
      const rows = (fixture.nativeElement as HTMLElement).querySelectorAll(`[data-testid="${prefix}-row"]`);
      expect(rows.length).withContext('both kinds merged into one list').toBe(2);

      (fixture.componentInstance as { onSearch(v: string): void }).onSearch('m');
      fixture.detectChanges();
      tick(400);
      const sent = http.match(() => true);
      const loud = sent.filter(r => !r.request.context.get(SKIP_LOADER)).map(r => r.request.url);
      const lists = sent.filter(isListRequest).map(r => r.request.url);
      sent.forEach(respond);

      expect(lists).withContext('search runs over the rows already loaded').toEqual([]);
      expect(loud).withContext('these raise the app-wide loader, which takes focus out of the search box').toEqual([]);
      fixture.destroy();
    }));
  });
}

describe('Connection lists — search keeps focus', () => {
  suite('Source Connections', SourceConnectionListComponent, 'src');
  suite('Destination Connections', DestinationConnectionListComponent, 'dest');
});
