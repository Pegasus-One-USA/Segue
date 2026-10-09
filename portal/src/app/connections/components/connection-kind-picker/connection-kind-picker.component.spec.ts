import { TestBed } from '@angular/core/testing';
import { ConnectionKindPickerComponent } from './connection-kind-picker.component';
import { ConnectionKindCard } from '../../connection-row.model';

describe('ConnectionKindPickerComponent', () => {
  const EPIC: ConnectionKindCard = { kind: 'ehr-read', value: 'Epic', label: 'Epic', sub: '', abbr: 'EP', color: 'red' };
  const DB: ConnectionKindCard = { kind: 'database', value: 'Database', label: 'SQL database', sub: '', abbr: 'SQL', color: 'blue' };

  function create() {
    TestBed.configureTestingModule({ imports: [ConnectionKindPickerComponent] });
    const fixture = TestBed.createComponent(ConnectionKindPickerComponent);
    fixture.componentRef.setInput('title', 'New source connection');
    fixture.componentRef.setInput('testIdPrefix', 'src');
    fixture.componentRef.setInput('groups', [
      { heading: 'EHR', cards: [EPIC] },
      { heading: 'Database', cards: [DB] },
      { heading: 'Empty', cards: [] },
    ]);
    fixture.detectChanges();
    return { picker: fixture.componentInstance, el: fixture.nativeElement as HTMLElement };
  }

  it('hides empty groups', () => {
    const { el } = create();
    const headings = Array.from(el.querySelectorAll('.sc-picker-group')).map(h => h.textContent!.trim());

    expect(headings).toEqual(['EHR', 'Database']);
  });

  it('marks each card with its value and kind, and emits the picked card', () => {
    const { picker, el } = create();
    const picked = jasmine.createSpy('picked');
    picker.picked.subscribe(picked);
    const cards = Array.from(el.querySelectorAll('[data-testid="src-vendor-card"]'));

    expect(cards.map(c => [c.getAttribute('data-vendor'), c.getAttribute('data-kind')])).toEqual([['Epic', 'ehr-read'], ['Database', 'database']]);
    (cards[1] as HTMLButtonElement).click();
    expect(picked).toHaveBeenCalledWith(DB);
  });

  it('closes on Escape, the backdrop and the close button', () => {
    const { picker, el } = create();
    const closed = jasmine.createSpy('closed');
    picker.closed.subscribe(closed);
    const backdrop = el.querySelector('.sc-modal-backdrop') as HTMLElement;

    backdrop.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    backdrop.click();
    (el.querySelector('.sc-picker-close') as HTMLButtonElement).click();
    (el.querySelector('.sc-picker-panel') as HTMLElement).click();

    expect(closed).toHaveBeenCalledTimes(3);
  });
});
