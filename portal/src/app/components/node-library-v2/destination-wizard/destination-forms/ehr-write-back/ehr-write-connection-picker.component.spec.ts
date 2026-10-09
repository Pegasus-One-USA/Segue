import { TestBed } from '@angular/core/testing';
import { FormControl } from '@angular/forms';
import { Component, input, output } from '@angular/core';
import { EhrWriteConnectionPickerComponent } from './ehr-write-connection-picker.component';
import { EhrRunMode, EhrWriteVendor } from './ehr-write-back.model';
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

/** "Write to" / "Test server": the write connections that fit, plus New connection for a role that may make one. */
describe('EhrWriteConnectionPickerComponent', () => {
  const ALL = ['sourceconnections.view', 'ehrwriteback.create', 'epic.create', 'genericfhir.create'];

  function render(options: { vendor?: EhrWriteVendor | null; runMode?: EhrRunMode; granted?: string[] } = {}) {
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
    fixture.componentRef.setInput('control', new FormControl<string | null>(''));
    fixture.componentRef.setInput('vendor', options.vendor === undefined ? 'Epic' : options.vendor);
    fixture.componentRef.setInput('runMode', options.runMode ?? 'dryRun');
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const newButton = () => el.querySelector<HTMLButtonElement>('[data-testid="ewb-new-connection"]');
    return { fixture, picker: fixture.componentInstance, el, newButton };
  }

  it('reads "Write to", and "Test server" in a test run', () => {
    expect(render().el.textContent).toContain('Write to');
    TestBed.resetTestingModule();
    expect(render({ runMode: 'test' }).el.textContent).toContain('Test server');
  });

  it('offers New connection only with the view right, ehrwriteback.create and the vendor\'s own create right', () => {
    expect(render().newButton()).not.toBeNull();
    for (const missing of ['sourceconnections.view', 'ehrwriteback.create', 'epic.create']) {
      TestBed.resetTestingModule();
      expect(render({ granted: ALL.filter(c => c !== missing) }).newButton()).withContext(missing).toBeNull();
    }
  });

  it('creates a FHIR server in a test run', () => {
    const { picker, newButton, fixture, el } = render({ runMode: 'test', granted: ['sourceconnections.view', 'ehrwriteback.create', 'genericfhir.create'] });
    expect(picker.createVendor()).toBe('GenericFhir');

    newButton()!.click();
    fixture.detectChanges();
    expect(el.querySelector('app-ehr-write-connection-create')).not.toBeNull();
  });

  it('passes on the created connection and closes the create form', () => {
    const { picker, fixture, el, newButton } = render();
    const created = jasmine.createSpy('created');
    picker.created.subscribe(created);
    newButton()!.click();
    fixture.detectChanges();

    const model = { id: 'n1' } as SourceConnectionModel;
    picker.onCreated(model);
    fixture.detectChanges();

    expect(created).toHaveBeenCalledWith(model);
    expect(el.querySelector('app-ehr-write-connection-create')).toBeNull();
  });

  it('says when the role cannot list write connections, with no New button', () => {
    const { fixture, el, newButton } = render();
    fixture.componentRef.setInput('loadError', 'noViewPermission');
    fixture.detectChanges();

    expect(el.textContent).toContain('You need permission to view Source Connections to choose a write connection.');
    expect(newButton()).toBeNull();
  });
});
