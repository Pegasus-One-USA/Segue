import { TestBed } from '@angular/core/testing';
import { DestinationConnectionFiltersComponent, EMPTY_DESTINATION_FILTERS } from './destination-connection-filters.component';
import { PermissionService } from '../../../auth/services/permission.service';

describe('DestinationConnectionFiltersComponent', () => {
  function create(opts: { showDestinations?: boolean; showWrite?: boolean; rowTypes?: string[] } = {}) {
    TestBed.configureTestingModule({
      imports: [DestinationConnectionFiltersComponent],
      providers: [{ provide: PermissionService, useValue: { hasPermission: () => true } }],
    });
    const fixture = TestBed.createComponent(DestinationConnectionFiltersComponent);
    fixture.componentRef.setInput('value', EMPTY_DESTINATION_FILTERS);
    fixture.componentRef.setInput('showDestinations', opts.showDestinations ?? true);
    fixture.componentRef.setInput('showWrite', opts.showWrite ?? true);
    fixture.componentRef.setInput('rowTypes', opts.rowTypes ?? []);
    fixture.detectChanges();
    const emitted = jasmine.createSpy('valueChange');
    fixture.componentInstance.valueChange.subscribe(emitted);
    const el = fixture.nativeElement as HTMLElement;
    return { el, emitted, type: el.querySelector('[data-testid="dest-filter-type"]') as HTMLSelectElement };
  }

  const values = (s: HTMLSelectElement) => Array.from(s.options).map(o => o.value);
  const groups = (s: HTMLSelectElement) => Array.from(s.querySelectorAll('optgroup')).map(g => g.label);

  it('offers both kinds, the catalog types and the EHR write vendors as filter keys', () => {
    const { type } = create();

    expect(values(type)).toContain('kind:destination');
    expect(values(type)).toContain('kind:ehr-write');
    expect(values(type)).toContain('destination:SqlServer');
    expect(values(type)).toContain('ehr-write:Healow');
    expect(groups(type)).toContain('EHR write connections');
  });

  it('emits the chosen key', () => {
    const { type, emitted } = create();

    type.value = 'ehr-write:Epic';
    type.dispatchEvent(new Event('change'));

    expect(emitted).toHaveBeenCalledWith({ type: 'ehr-write:Epic', status: '' });
  });

  it('adds an "Other types" group for a type present in the list but not offered by the catalog', () => {
    const { type } = create({ rowTypes: ['Sftp', 'SqlServer', 'EhrWriteBack'] });
    const other = type.querySelector('optgroup[label="Other types"]')!;
    const otherValues = Array.from(other.querySelectorAll('option')).map(o => o.value);

    expect(otherValues).toEqual(['destination:Sftp', 'destination:EhrWriteBack']);
    expect(other.textContent).toContain('EHR write-back');
  });

  it('leaves out the groups of a kind the role cannot list', () => {
    const { type } = create({ showWrite: false });

    expect(values(type)).not.toContain('kind:ehr-write');
    expect(values(type)).not.toContain('kind:destination');
    expect(groups(type)).not.toContain('EHR write connections');
    expect(values(type)).toContain('destination:SqlServer');
  });

  it('shows only the EHR write group when destinations cannot be listed', () => {
    const { type } = create({ showDestinations: false });

    expect(groups(type)).toEqual(['EHR write connections']);
  });
});
