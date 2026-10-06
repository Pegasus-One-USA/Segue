import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { NEVER, of } from 'rxjs';
import { SystemSettingListComponent } from './system-setting-list.component';
import { ISystemSettingsService } from '../../services/i-system-settings.service';
import { HapiTerminologyConfigurationService } from '../../services/hapi-terminology-configuration.service';
import { TerminologyStatusHubService } from '../../services/terminology-status-hub.service';
import { AuthStore } from '../../../auth/store/auth.store';
import { IRoleService } from '../../../user-management/services/i-role.service';
import { makeTestUser } from '../../../auth/testing/auth-test-helpers';

/**
 * The Terminology Server section and the settings table below it used to share ONE search: the page's search()
 * was bound into <app-hapi-terminology-table [searchTerm]>, so a term typed into the table's box filtered both.
 * Each now owns its own search box and state; neither term reaches the other section.
 */
describe('System Settings — Terminology Server and settings table searches are independent', () => {
  let fixture: ReturnType<typeof TestBed.createComponent<SystemSettingListComponent>>;
  let root: HTMLElement;

  const config = (code: string, displayName: string) => ({
    code, displayName, schedulerEnabled: true, frequency: 'Daily', frequencyOptions: ['Daily'], executionTime: '02:00',
    lastRunUtc: null, credentials: [], downloadApiUrl: null,
  });

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [SystemSettingListComponent],
      providers: [
        AuthStore,
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: IRoleService, useValue: { getRoles: () => of([]) } },
        { provide: ISystemSettingsService, useValue: {
            getAll: () => of([
              { key: 'Caching:TerminologyTtlMinutes', value: '60', description: 'Terminology cache TTL.' },
              { key: 'Alerts:EvaluationIntervalSeconds', value: '60', description: 'Alert evaluation cadence.' },
            ]) } },
        { provide: HapiTerminologyConfigurationService, useValue: {
            getAll: () => of([config('loinc', 'LOINC'), config('snomed', 'SNOMED CT'), config('rxnorm', 'RxNorm')]) } },
        { provide: TerminologyStatusHubService, useValue: { ensureConnected: () => undefined, statusChanged$: NEVER, reconnected$: NEVER } },
      ],
    });
    TestBed.inject(AuthStore).setUser(makeTestUser([], 'SuperAdmin'));
    fixture = TestBed.createComponent(SystemSettingListComponent);
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
  });

  const terminologySearch = () => root.querySelector<HTMLInputElement>('app-hapi-terminology-table input[type="search"]')!;
  const tableSearch = () => root.querySelector<HTMLInputElement>('input[placeholder="Search by key or description…"]')!;

  function type(input: HTMLInputElement, value: string): void {
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  const terminologyRows = () =>
    Array.from(root.querySelectorAll('app-hapi-terminology-table tr[mat-row]')).map(r => r.textContent!.trim());
  const terminologyNoMatch = () => root.querySelector('app-hapi-terminology-table .no-match-row')?.textContent?.trim();
  /** Setting keys the settings table currently shows. */
  const tableKeys = () => {
    const shown = JSON.stringify(fixture.componentInstance.groupedRows());
    return ['Caching:TerminologyTtlMinutes', 'Alerts:EvaluationIntervalSeconds'].filter(key => shown.includes(key));
  };

  it('starts with both sections unfiltered, each with its own search box', () => {
    expect(terminologySearch()).withContext('Terminology Server has its own search box').toBeTruthy();
    expect(tableSearch()).toBeTruthy();
    expect(terminologySearch()).not.toBe(tableSearch());
    expect(terminologyRows().length).toBe(3);
    expect(tableKeys().length).toBe(2);
  });

  it('1. Terminology Server search filters only the Terminology Server', () => {
    type(terminologySearch(), 'loinc');

    expect(terminologyRows().length).toBe(1);
    expect(terminologyRows()[0]).toContain('LOINC');
    expect(tableKeys().length).withContext('settings table untouched').toBe(2);
    expect(tableSearch().value).toBe('');
  });

  it('1b. a term matching nothing ("Epic") empties only the Terminology Server, with a message', () => {
    type(terminologySearch(), 'Epic');

    expect(terminologyRows().length).toBe(0);
    expect(terminologyNoMatch()).toBe('No code systems match "Epic".');
    expect(tableKeys().length).toBe(2);
  });

  it('2. settings table search filters only the table', () => {
    type(tableSearch(), 'Caching');

    expect(tableKeys()).toEqual(['Caching:TerminologyTtlMinutes']);
    expect(terminologyRows().length).withContext('Terminology Server untouched').toBe(3);
    expect(terminologySearch().value).toBe('');
  });

  it('5. both at once keep their own values and results', () => {
    type(terminologySearch(), 'snomed');
    type(tableSearch(), 'Alerts');

    expect(terminologyRows().length).toBe(1);
    expect(terminologyRows()[0]).toContain('SNOMED CT');
    expect(tableKeys()).toEqual(['Alerts:EvaluationIntervalSeconds']);
    expect(terminologySearch().value).toBe('snomed');
    expect(tableSearch().value).toBe('Alerts');
  });

  it('3. clearing the Terminology Server search restores it and leaves the table filtered', () => {
    type(terminologySearch(), 'loinc');
    type(tableSearch(), 'Caching');

    type(terminologySearch(), '');

    expect(terminologyRows().length).toBe(3);
    expect(tableKeys()).toEqual(['Caching:TerminologyTtlMinutes']);
  });

  it('4. clearing the table search restores it and leaves the Terminology Server filtered', () => {
    type(terminologySearch(), 'rxnorm');
    type(tableSearch(), 'Caching');

    type(tableSearch(), '');

    expect(tableKeys().length).toBe(2);
    expect(terminologyRows().length).toBe(1);
    expect(terminologyRows()[0]).toContain('RxNorm');
  });

  it('6. changing one search repeatedly never moves the other section', () => {
    type(tableSearch(), 'Alerts');
    for (const value of ['l', 'lo', 'loi', 'loinc', 'snomed', '']) {
      type(terminologySearch(), value);
      expect(tableKeys()).withContext(`table after terminology search "${value}"`).toEqual(['Alerts:EvaluationIntervalSeconds']);
    }
    for (const value of ['C', 'Ca', 'Caching', '']) {
      type(tableSearch(), value);
      expect(terminologyRows().length).withContext(`terminology after table search "${value}"`).toBe(3);
    }
  });
});
