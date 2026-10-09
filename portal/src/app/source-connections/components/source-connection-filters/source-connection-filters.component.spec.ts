import { TestBed } from '@angular/core/testing';
import {
  EMPTY_SOURCE_FILTERS,
  SourceConnectionFiltersComponent,
  SourceConnectionFilterValue,
  sourceFilterKeys,
} from './source-connection-filters.component';

describe('SourceConnectionFiltersComponent', () => {
  function create(value: SourceConnectionFilterValue, showKind = true) {
    TestBed.configureTestingModule({ imports: [SourceConnectionFiltersComponent] });
    const fixture = TestBed.createComponent(SourceConnectionFiltersComponent);
    fixture.componentRef.setInput('value', value);
    fixture.componentRef.setInput('showKind', showKind);
    fixture.componentRef.setInput('ehrOptions', [{ value: 'Epic', label: 'Epic' }]);
    fixture.componentRef.setInput('audienceOptions', [{ value: 'Backend', label: 'Backend System' }]);
    fixture.detectChanges();
    const emitted = jasmine.createSpy('valueChange');
    fixture.componentInstance.valueChange.subscribe(emitted);
    return { el: fixture.nativeElement as HTMLElement, emitted };
  }

  const select = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLSelectElement;
  const choose = (s: HTMLSelectElement, value: string) => {
    s.value = value;
    s.dispatchEvent(new Event('change'));
  };

  it('turns the choices into row filter keys', () => {
    expect(sourceFilterKeys(EMPTY_SOURCE_FILTERS)).toEqual([]);
    expect(sourceFilterKeys({ kind: 'ehr-read', vendor: 'Epic', audience: 'Backend', status: 'true' }))
      .toEqual(['kind:ehr-read', 'vendor:Epic', 'audience:Backend']);
  });

  it('keeps the raw audience value on the option', () => {
    const { el, emitted } = create(EMPTY_SOURCE_FILTERS);

    choose(select(el, 'src-filter-audience'), 'Backend');

    expect(emitted).toHaveBeenCalledWith({ ...EMPTY_SOURCE_FILTERS, audience: 'Backend' });
  });

  it('choosing Database clears EHR, audience and status', () => {
    const { el, emitted } = create({ kind: '', vendor: 'Epic', audience: 'Backend', status: 'true' });

    choose(select(el, 'src-filter-kind'), 'database');

    expect(emitted).toHaveBeenCalledWith({ kind: 'database', vendor: '', audience: '', status: '' });
  });

  it('disables EHR, audience and status while Database is chosen', () => {
    const { el } = create({ ...EMPTY_SOURCE_FILTERS, kind: 'database' });

    expect(select(el, 'src-filter-ehr').disabled).toBeTrue();
    expect(select(el, 'src-filter-audience').disabled).toBeTrue();
    expect(select(el, 'src-filter-status').disabled).toBeTrue();
  });

  it('hides the kind select when the role can list one kind only', () => {
    const { el } = create(EMPTY_SOURCE_FILTERS, false);

    expect(select(el, 'src-filter-kind')).toBeNull();
  });
});
