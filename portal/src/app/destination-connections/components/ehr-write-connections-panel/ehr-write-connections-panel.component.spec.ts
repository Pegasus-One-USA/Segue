import { TestBed } from '@angular/core/testing';
import { WritableSignal, signal } from '@angular/core';
import { of } from 'rxjs';
import { EhrWriteConnectionsPanelComponent } from './ehr-write-connections-panel.component';
import { ISourceConnectionService } from '../../../source-connections/services/i-source-connection.service';
import { SourceConnectionModel } from '../../../source-connections/models/source-connection.model';
import { WizardService } from '../../../services/wizard.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';

/**
 * EHR write connections on Destination Connections: lists only connections that can write (access=write), offers
 * Epic / eClinicalWorks / athenahealth / FHIR server, and opens the shared vendor form in write purpose.
 */
describe('EhrWriteConnectionsPanelComponent', () => {
  const ATHENA = {
    id: 'a1', name: 'athena write', sourceSystemType: 'Athenahealth', baseUrl: 'https://api.athena.example/fhir/r4',
    isEnabled: true, access: 'Write', vendorWriteApisActivated: true,
    authentication: { authenticationType: 'OAuthClientCredentials', scopes: [] },
  } as SourceConnectionModel;
  const HAPI = {
    id: 'h1', name: 'Local HAPI', sourceSystemType: 'GenericFhir', baseUrl: 'https://hapi.example/fhir',
    isEnabled: true, access: 'ReadWrite', authentication: { authenticationType: 'None', scopes: [] },
  } as SourceConnectionModel;

  let getAll: jasmine.Spy;
  let wiz: {
    isOpen: WritableSignal<boolean>;
    wizardMode: WritableSignal<string>;
    purpose: WritableSignal<string>;
    ehrType: WritableSignal<string>;
    saved: WritableSignal<number>;
    openEntity: jasmine.Spy;
    close: jasmine.Spy;
  };

  function create(granted: (code: string) => boolean = () => true) {
    getAll = jasmine.createSpy('getAll').and.returnValue(of([ATHENA, HAPI]));
    wiz = {
      isOpen: signal(false), wizardMode: signal('canvas'), purpose: signal('read'), ehrType: signal('Epic'),
      saved: signal(0), openEntity: jasmine.createSpy('openEntity'), close: jasmine.createSpy('close'),
    };
    const permissions = {
      isReady: () => true,
      hasPermission: granted,
      hasAll: (codes: readonly string[]) => codes.every(granted),
      matches: (mode: string, codes: readonly string[]) => mode === 'all' ? codes.every(granted) : codes.some(granted),
    };
    TestBed.configureTestingModule({
      imports: [EhrWriteConnectionsPanelComponent],
      providers: [
        { provide: ISourceConnectionService, useValue: { getAll } },
        { provide: WizardService, useValue: wiz },
        { provide: PermissionService, useValue: permissions },
        { provide: PermissionActionGuard, useValue: { ensure: () => true } },
      ],
    });
    const fixture = TestBed.createComponent(EhrWriteConnectionsPanelComponent);
    fixture.detectChanges();
    return { fixture, panel: fixture.componentInstance, el: fixture.nativeElement as HTMLElement };
  }

  it('lists the connections that can write, with EHR label and activation status', () => {
    const { el } = create();

    expect(getAll).toHaveBeenCalledWith('write');
    const rows = Array.from(el.querySelectorAll('[data-testid="ewc-row"]')).map(r => r.textContent ?? '');
    expect(rows.length).toBe(2);
    expect(rows[0]).toContain('athenahealth');
    expect(rows[0]).toContain('Activated');
    expect(rows[1]).toContain('FHIR server');
  });

  it('offers Epic, eClinicalWorks, athenahealth and FHIR server cards', () => {
    const { fixture, el } = create();

    (el.querySelector('[data-testid="ewc-new"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    const vendors = Array.from(el.querySelectorAll('[data-testid="ewc-vendor-card"]')).map(c => c.getAttribute('data-vendor'));
    expect(vendors).toEqual(['Epic', 'Healow', 'Athenahealth', 'GenericFhir']);
    expect(el.textContent).toContain('eClinicalWorks');
  });

  it('opens the vendor form in write purpose for a new connection', () => {
    const { fixture, el } = create();
    (el.querySelector('[data-testid="ewc-new"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    (el.querySelector('[data-vendor="Healow"]') as HTMLButtonElement).click();

    expect(wiz.openEntity).toHaveBeenCalledWith(null, { purpose: 'write' });
    expect(wiz.ehrType()).toBe('Healow');
  });

  it('edits an existing connection in write purpose, and a FHIR server in its own form', () => {
    const { fixture, panel } = create();

    panel.openEdit(ATHENA);
    expect(wiz.openEntity).toHaveBeenCalledWith(ATHENA, { readonly: false, purpose: 'write' });

    panel.openEdit(HAPI);
    fixture.detectChanges();
    expect(panel.genericFhirEditing()).toBe(HAPI);
    expect((fixture.nativeElement as HTMLElement).querySelector('app-generic-fhir-write-connection-form')).not.toBeNull();
  });

  it('hides New and Edit without the EHR Write-Back rights', () => {
    const { el } = create(code => code === 'ehrwriteback.view' || code === 'sourceconnections.view');

    expect(el.querySelector('[data-testid="ewc-new"]')).toBeNull();
    expect(el.querySelector('[data-testid="ewc-edit"]')).toBeNull();
    expect(el.querySelectorAll('[data-testid="ewc-row"]').length).toBe(2);
  });

  it('is hidden and does not load without sourceconnections.view (the list endpoint needs it)', () => {
    const { el } = create(code => code === 'ehrwriteback.view');

    expect(getAll).not.toHaveBeenCalled();
    expect(el.querySelector('.ewc-panel')).toBeNull();
  });

  it('reloads after a vendor form save', () => {
    const { fixture } = create();
    getAll.calls.reset();

    wiz.saved.set(1);
    fixture.detectChanges();

    expect(getAll).toHaveBeenCalledWith('write');
  });
});
