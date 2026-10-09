import { Type } from '@angular/core';
import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { SourceConnectionListComponent } from './source-connection-list.component';
import { DestinationConnectionListComponent } from '../../../destination-connections/pages/destination-connection-list/destination-connection-list.component';
import { SKIP_LOADER } from '../../../core/loading.interceptor';
import { AuthStore } from '../../../auth/store/auth.store';
import { ISourceConnectionService } from '../../services/i-source-connection.service';
import { ApiSourceConnectionService } from '../../services/api-source-connection.service';

/**
 * Typing in the Source / Destination Connections search lost focus after every keystroke. The search request
 * itself is silent (SKIP_LOADER), but each list then fired a follow-up check — Source: which connections are in
 * use; Destination: each row's execution-history flag — WITHOUT it. That raised the app-wide loader, which makes
 * the page inert and takes focus out of the box. Mapping Profiles has no follow-up, which is why it was fine.
 * Every request a search keystroke causes must skip the loader.
 */
function suite(label: string, list: Type<unknown>, row: Record<string, unknown>): void {
  describe(label, () => {
    it('sends every request a search triggers without the app-wide loader', fakeAsync(() => {
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
      // Answer the initial page load (and anything else on open) with one row.
      http.match(() => true).forEach(r => r.flush(r.request.url.includes('usage') ? [] : { items: [row], totalCount: 1, page: 1, pageSize: 10 }));
      tick(1000);
      http.match(() => true).forEach(r => r.flush({ hasExecutionHistory: false }));

      (fixture.componentInstance as { onSearch(v: string): void }).onSearch('m');
      tick(400);   // the search debounce
      const listRequests = http.match(() => true);
      listRequests.forEach(r => r.flush(r.request.url.includes('usage') ? [] : { items: [row], totalCount: 1, page: 1, pageSize: 10 }));
      tick(100);
      const followUps = http.match(() => true);
      const all = [...listRequests, ...followUps];
      const loud = all.filter(r => !r.request.context.get(SKIP_LOADER)).map(r => r.request.url);
      followUps.forEach(r => r.flush(r.request.url.includes('usage') ? [] : { hasExecutionHistory: false }));
      expect(loud).withContext('these raise the app-wide loader, which takes focus out of the search box').toEqual([]);
      fixture.destroy();
    }));
  });
}

describe('Connection lists — search keeps focus', () => {
  suite('Source Connections', SourceConnectionListComponent, { id: 's1', name: 'ECW', sourceSystemType: 'Healow', applicationType: 'BackendServices', baseUrl: 'https://x', clientId: 'c', isEnabled: true });
  suite('Destination Connections', DestinationConnectionListComponent, { id: 'd1', name: 'Medplum 1', destinationType: 'Medplum', isEnabled: true });
});
