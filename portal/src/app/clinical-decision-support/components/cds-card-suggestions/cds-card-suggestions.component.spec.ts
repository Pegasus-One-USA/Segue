import { TestBed } from '@angular/core/testing';
import { CdsCardSuggestionsComponent } from './cds-card-suggestions.component';
import { CdsSuggestion } from '../../models/cds.models';

describe('CdsCardSuggestionsComponent', () => {
  const REMOVE: CdsSuggestion = {
    label: 'Remove ibuprofen', uuid: 's1',
    actions: [{ type: 'delete', description: 'Removes the draft order.', resourceId: 'MedicationRequest/med-1' }],
  };
  const ADD: CdsSuggestion = {
    label: 'Order naproxen', uuid: 's2', isRecommended: true,
    actions: [{
      type: 'create',
      resource: {
        resourceType: 'MedicationRequest', status: 'draft', intent: 'proposal',
        medicationCodeableConcept: { coding: [{ system: 'urn:oid:2.16.840.1.113883.6.88', code: '198013', display: 'Naproxen 250 MG' }] },
      },
    }],
  };
  const PROBLEM: CdsSuggestion = {
    label: 'Add CKD to the problem list', uuid: 's3',
    actions: [{
      type: 'create',
      resource: { resourceType: 'Condition', code: { coding: [{ system: 'urn:oid:2.16.840.1.113883.6.90', code: 'N18.4' }] } },
    }],
  };

  function render(suggestions: CdsSuggestion[]) {
    TestBed.configureTestingModule({ imports: [CdsCardSuggestionsComponent] });
    const fixture = TestBed.createComponent(CdsCardSuggestionsComponent);
    fixture.componentRef.setInput('suggestions', suggestions);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const buttons = () => Array.from(el.querySelectorAll('[data-testid="cds-suggestion"]')) as HTMLButtonElement[];
    const previews = () => Array.from(el.querySelectorAll('[data-testid="cds-suggestion-preview"]'));
    const click = (i: number) => {
      buttons()[i].click();
      fixture.detectChanges();
    };
    return { fixture, el, buttons, previews, click };
  }

  it('draws one button per fix, with the recommended one ticked first', () => {
    const { buttons, previews } = render([REMOVE, ADD]);

    expect(buttons().map(b => b.textContent?.includes('Recommended'))).toEqual([false, true]);
    expect(buttons().map(b => b.getAttribute('aria-pressed'))).toEqual(['false', 'true']);
    expect(previews().length).toBe(1);
    expect(previews()[0].textContent).toContain('Order naproxen');
  });

  it('previews a delete as removing the named draft order', () => {
    const { click, previews } = render([REMOVE]);
    expect(previews().length).toBe(0);

    click(0);

    const text = previews()[0].textContent ?? '';
    expect(text).toContain('The EHR would remove the draft');
    expect(text).toContain('MedicationRequest/med-1');
    expect(text).toContain('Removes the draft order.');
  });

  it('previews a create with its status, code, system and the exact resource', () => {
    const { previews } = render([ADD]);
    const text = previews()[0].textContent ?? '';
    expect(text).toContain('The EHR would add a draft');
    expect(text).toContain('Naproxen 250 MG');
    expect(text).toContain('198013');
    expect(text).toContain('urn:oid:2.16.840.1.113883.6.88');
    const json = JSON.parse(previews()[0].querySelector('pre')?.textContent ?? '{}');
    expect(json).toEqual(ADD.actions![0].resource!);
  });

  it('falls back to the code when the coding has no display, and omits "draft" when no status is set', () => {
    const { click, previews } = render([PROBLEM]);
    click(0);
    const text = previews()[0].textContent ?? '';
    expect(text).toContain('N18.4');
    expect(text).not.toContain('add a draft');
  });

  it('unticks a fix', () => {
    const { click, previews, buttons } = render([ADD]);
    click(0);
    expect(previews().length).toBe(0);
    expect(buttons()[0].getAttribute('aria-pressed')).toBe('false');
  });

  it('says so for a fix with no action', () => {
    const { click, previews } = render([{ label: 'Empty' }]);
    click(0);
    expect(previews()[0].textContent).toContain('This fix has no action.');
  });
});
