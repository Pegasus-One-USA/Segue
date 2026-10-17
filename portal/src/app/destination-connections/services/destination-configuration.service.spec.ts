import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DestinationConfigurationService } from './destination-configuration.service';
import { DESTINATION_ENDPOINTS } from '../../core/api-endpoints';

/**
 * Guards the narrow profile-only write the destination wizard's "De-identification" tab depends on.
 *
 * Regression context: the wizard used to have no way to persist a policy chosen on Step 3 — Step 1's
 * provisionDestinationConnection() early-returns for reused connections and runs before that tab is reachable —
 * so DestinationConfigurations.DeIdentificationProfileId stayed NULL and the picker reset to "None" on reopen.
 * These assert the contract that fix relies on, including that clearing to "None" is actually transmitted.
 */
describe('DestinationConfigurationService.setDeIdentificationProfile', () => {
  const destinationId = '415c2216-d4b0-4b24-ac4c-024c456bb981';
  const profileId = 'b7f3c9a1-2e4d-4f8b-9c6a-1d5e7f2a3b4c';

  let service: DestinationConfigurationService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        DestinationConfigurationService,
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });

    service = TestBed.inject(DestinationConfigurationService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('PUTs the profile id to the destination-scoped de-identification endpoint', () => {
    service.setDeIdentificationProfile(destinationId, profileId).subscribe();

    const req = http.expectOne(DESTINATION_ENDPOINTS.deIdentificationProfile(destinationId));
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ deIdentificationProfileId: profileId });
    req.flush({ id: destinationId, deIdentificationProfileId: profileId });
  });

  it('transmits null so selecting "None" actually clears the assignment', () => {
    // The bug this guards against is a truthiness check dropping the null and turning "clear the policy" into
    // a silent no-op — leaving redaction applied to a destination the user just switched off.
    service.setDeIdentificationProfile(destinationId, null).subscribe();

    const req = http.expectOne(DESTINATION_ENDPOINTS.deIdentificationProfile(destinationId));
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ deIdentificationProfileId: null });
    req.flush({ id: destinationId, deIdentificationProfileId: null });
  });

  it('targets a route distinct from the full destination update', () => {
    // A profile-only change must not go through byId(): that PUT runs the full create-request validator, which
    // demands KeyVaultName/SecretName the wizard never re-displays for a stored secret.
    expect(DESTINATION_ENDPOINTS.deIdentificationProfile(destinationId))
      .not.toBe(DESTINATION_ENDPOINTS.byId(destinationId));
    expect(DESTINATION_ENDPOINTS.deIdentificationProfile(destinationId))
      .toContain('/deidentification-profile');
  });
});

/** The Destination Connections page merges every destination with the EHR write connections and pages them itself. */
describe('DestinationConfigurationService.getAllPages', () => {
  it('reads every page of the paged list, in order', () => {
    TestBed.configureTestingModule({
      providers: [DestinationConfigurationService, provideHttpClient(), provideHttpClientTesting()],
    });
    const service = TestBed.inject(DestinationConfigurationService);
    const http = TestBed.inject(HttpTestingController);
    let received: unknown;

    service.getAllPages(2).subscribe(list => (received = list));
    const first = http.expectOne(r => r.url === DESTINATION_ENDPOINTS.paged && r.params.get('page') === '1');
    expect(first.request.params.get('pageSize')).toBe('2');
    first.flush({ items: [{ id: 'd1' }, { id: 'd2' }], totalCount: 3, page: 1, pageSize: 2 });
    http.expectOne(r => r.url === DESTINATION_ENDPOINTS.paged && r.params.get('page') === '2')
      .flush({ items: [{ id: 'd3' }], totalCount: 3, page: 2, pageSize: 2 });

    expect(received).toEqual([{ id: 'd1' }, { id: 'd2' }, { id: 'd3' }]);
    http.verify();
  });

  it('stops after one request when everything fits', () => {
    TestBed.configureTestingModule({
      providers: [DestinationConfigurationService, provideHttpClient(), provideHttpClientTesting()],
    });
    const service = TestBed.inject(DestinationConfigurationService);
    const http = TestBed.inject(HttpTestingController);
    let received: unknown;

    service.getAllPages().subscribe(list => (received = list));
    http.expectOne(r => r.url === DESTINATION_ENDPOINTS.paged).flush({ items: [], totalCount: 0, page: 1, pageSize: 100 });

    expect(received).toEqual([]);
    http.verify();
  });
});
