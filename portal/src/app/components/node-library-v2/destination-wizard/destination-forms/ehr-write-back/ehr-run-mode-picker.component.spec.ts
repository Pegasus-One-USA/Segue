import { TestBed } from '@angular/core/testing';
import { FormControl } from '@angular/forms';
import { EhrRunModePickerComponent } from './ehr-run-mode-picker.component';
import { EhrRunMode, EhrWriteVendor, WritableTarget } from './ehr-write-back.model';

function target(extra: Partial<WritableTarget> = {}): WritableTarget {
  return {
    id: 'c1', name: 'Epic prod', vendor: 'Epic', resourceTypes: ['AllergyIntolerance'], liveTypes: ['AllergyIntolerance'],
    awaitingActivationTypes: [], offersHolderEncounter: false, cloneModeEnabled: false, testableVendors: [], optInApis: [],
    departmentId: null, capabilities: [], ...extra,
  };
}

/** Run mode: the modes a vendor offers, and what the chosen one will really do over the chosen connection. */
describe('EhrRunModePickerComponent', () => {
  function render(vendor: EhrWriteVendor | null, mode: EhrRunMode, t: WritableTarget | null = null) {
    TestBed.configureTestingModule({ imports: [EhrRunModePickerComponent] });
    const fixture = TestBed.createComponent(EhrRunModePickerComponent);
    const control = new FormControl<EhrRunMode | null>(mode);
    fixture.componentRef.setInput('control', control);
    fixture.componentRef.setInput('vendor', vendor);
    fixture.componentRef.setInput('runMode', mode);
    fixture.componentRef.setInput('target', t);
    fixture.componentRef.setInput('connectionName', 'Local HAPI');
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return {
      control,
      el,
      modes: () => Array.from(el.querySelectorAll<HTMLInputElement>('input[type="radio"]')).map(r => r.dataset['mode']),
      text: () => el.textContent?.replace(/\s+/g, ' ') ?? '',
    };
  }

  it('offers Live, Dry run and Test on a FHIR server for a vendor a test server can stand in for', () => {
    const { modes, text } = render('Athenahealth', 'dryRun');
    expect(modes()).toEqual(['live', 'dryRun', 'test']);
    expect(text()).toContain('Live: write into athenahealth');
    expect(text()).toContain('Dry run: check every record, send nothing');
    expect(text()).toContain('Test on a FHIR server: send exactly what athenahealth would get to a test server instead');
    expect(text()).toContain('Start with a dry run.');
  });

  it('a FHIR server, or no vendor yet, has no Test', () => {
    expect(render('GenericFhir', 'dryRun').modes()).toEqual(['live', 'dryRun']);
    TestBed.resetTestingModule();
    const unknown = render(null, 'dryRun');
    expect(unknown.modes()).toEqual(['live', 'dryRun']);
    expect(unknown.text()).toContain('Pick a connection to see every run mode.');
  });

  it('a click sets the control', () => {
    const { el, control } = render('Epic', 'dryRun');
    el.querySelector<HTMLInputElement>('input[data-mode="live"]')!.click();
    expect(control.value).toBe('live');
  });

  it('warns what a test run reaches', () => {
    expect(render('Epic', 'test', target()).text())
      .toContain('Test run: the types you select under Resource types are written to Local HAPI, shaped exactly as Epic would receive them. Nothing reaches Epic.');
  });

  it('warns that Live writes into the connection', () => {
    expect(render('Epic', 'live', target()).text()).toContain('Live: the types you select under Resource types will be written into Epic prod.');
  });

  it('says a Live run still only checks until the vendor write APIs are activated', () => {
    const t = target({ vendor: 'Healow', name: 'eCW prod', liveTypes: [], awaitingActivationTypes: ['AllergyIntolerance'] });
    expect(render('Healow', 'live', t).text()).toContain('eCW prod does not have the eClinicalWorks write APIs marked as activated');
  });

  it('says a vendor that takes dry runs only still only checks', () => {
    const t = target({ vendor: 'Athenahealth', liveTypes: [] });
    expect(render('Athenahealth', 'live', t).text()).toContain('athenahealth accepts dry runs only for now');
  });

  it('warns nothing in a dry run', () => {
    expect(render('Epic', 'dryRun', target()).el.querySelector('.dw-hint--warn')).toBeNull();
  });
});
