import { TestBed } from '@angular/core/testing';
import { FormControl } from '@angular/forms';
import { Component, input, output } from '@angular/core';
import { EhrWriteConnectionPickerComponent } from './ehr-write-connection-picker.component';
import { EhrWriteVendor, WritableTarget } from './ehr-write-back.model';
import { PermissionService } from '../../../../../auth/services/permission.service';
import { SourceConnectionModel } from '../../../../../source-connections/models/source-connection.model';
import { EhrWriteConnectionCreateComponent } from '../../../../../destination-connections/components/ehr-write-connection-create/ehr-write-connection-create.component';

/** Stands in for the shared create component, which opens the real vendor forms. */
@Component({ selector: 'app-ehr-write-connection-create', standalone: true, template: '' })
class FakeCreateComponent {
  readonly vendor = input<EhrWriteVendor | null>(null);
  readonly created = output<SourceConnectionModel>();
  readonly cancelled = output<void>();
}

function target(id: string, vendor: string, testableVendors: string[] = []): WritableTarget {
  return {
    id, name: id, vendor, resourceTypes: ['AllergyIntolerance'], liveTypes: [], awaitingActivationTypes: [],
    offersHolderEncounter: false, cloneModeEnabled: false, testableVendors, optInApis: [], departmentId: null,
    capabilities: [],
  };
}

/** Step 1's one "Connection" dropdown: the EHR's own connections and its test servers, plus New for a role that may. */
describe('EhrWriteConnectionPickerComponent', () => {
  const ALL = ['sourceconnections.view', 'ehrwriteback.create', 'epic.create', 'genericfhir.create'];

  function render(options: { vendor?: EhrWriteVendor | null; granted?: string[] } = {}) {
    const granted = new Set(options.granted ?? ALL);
    TestBed.configureTestingModule({
      imports: [EhrWriteConnectionPickerComponent],
      providers: [{
        provide: PermissionService,
        useValue: {
          hasPermission: (c: string) => granted.has(c),
          hasAll: (codes: string[]) => codes.every(c => granted.has(c)),
        },
      }],
    });
    TestBed.overrideComponent(EhrWriteConnectionPickerComponent, {
      remove: { imports: [EhrWriteConnectionCreateComponent] },
      add: { imports: [FakeCreateComponent] },
    });
    const fixture = TestBed.createComponent(EhrWriteConnectionPickerComponent);
    const control = new FormControl<string | null>('');
    fixture.componentRef.setInput('control', control);
    fixture.componentRef.setInput('vendor', options.vendor === undefined ? 'Epic' : options.vendor);
    fixture.componentRef.setInput('own', [target('epic-prod', 'Epic'), target('epic-2', 'Epic')]);
    fixture.componentRef.setInput('testServers', [target('hapi', 'GenericFhir', ['Epic'])]);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const button = (id: string) => el.querySelector<HTMLButtonElement>(`[data-testid="${id}"]`);
    return { fixture, picker: fixture.componentInstance, el, button, control };
  }

  it('is labelled Connection and groups the EHR and its test servers, choosing nothing', () => {
    const { el, control } = render();
    expect(el.querySelector('label')?.textContent).toContain('Connection');
    const groups = Array.from(el.querySelectorAll('optgroup')).map(g => g.label);
    expect(groups).toEqual(['Epic', 'Test servers (receive exactly what Epic would)']);
    expect(Array.from(el.querySelectorAll<HTMLOptionElement>('optgroup[label="Epic"] option')).map(o => o.value))
      .toEqual(['epic-prod', 'epic-2']);
    expect(control.value).toBe('');
  });

  it('without a vendor lists every connection flat', () => {
    const { el } = render({ vendor: null });
    expect(el.querySelectorAll('optgroup').length).toBe(0);
    expect(el.querySelectorAll('option[value="epic-prod"]').length).toBe(1);
  });

  it('offers a new EHR connection and a new test server, each only with its rights', () => {
    expect(render().button('ewb-new-connection')).not.toBeNull();
    TestBed.resetTestingModule();
    expect(render().button('ewb-new-test-server')).not.toBeNull();
    for (const missing of ['sourceconnections.view', 'ehrwriteback.create', 'epic.create']) {
      TestBed.resetTestingModule();
      expect(render({ granted: ALL.filter(c => c !== missing) }).button('ewb-new-connection')).withContext(missing).toBeNull();
    }
    TestBed.resetTestingModule();
    expect(render({ granted: ALL.filter(c => c !== 'genericfhir.create') }).button('ewb-new-test-server')).toBeNull();
  });

  it('creates a test server from New test server', () => {
    const { picker, button, fixture, el } = render();
    button('ewb-new-test-server')!.click();
    fixture.detectChanges();
    expect(picker.creating()).toBe('GenericFhir');
    expect(el.querySelector('app-ehr-write-connection-create')).not.toBeNull();
  });

  it('passes on the created connection and closes the create form', () => {
    const { picker, fixture, el, button } = render();
    const created = jasmine.createSpy('created');
    picker.created.subscribe(created);
    button('ewb-new-connection')!.click();
    fixture.detectChanges();

    const model = { id: 'n1' } as SourceConnectionModel;
    picker.onCreated(model);
    fixture.detectChanges();

    expect(created).toHaveBeenCalledWith(model);
    expect(el.querySelector('app-ehr-write-connection-create')).toBeNull();
  });

  it('says when the role cannot list write connections, with no New button', () => {
    const { fixture, el, button } = render();
    fixture.componentRef.setInput('loadError', 'noViewPermission');
    fixture.detectChanges();

    expect(el.textContent).toContain('You need permission to view Source Connections to choose a write connection.');
    expect(button('ewb-new-connection')).toBeNull();
  });
});
