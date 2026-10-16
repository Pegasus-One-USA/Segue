import { TestBed } from '@angular/core/testing';
import { CdsCardPreviewComponent } from './cds-card-preview.component';
import { CdsCard } from '../../models/cds.models';

describe('CdsCardPreviewComponent', () => {
  function render(card: CdsCard): HTMLElement {
    TestBed.configureTestingModule({ imports: [CdsCardPreviewComponent] });
    const fixture = TestBed.createComponent(CdsCardPreviewComponent);
    fixture.componentRef.setInput('card', card);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the indicator, summary, detail and source', () => {
    const el = render({
      summary: 'Allergy to penicillin', detail: 'Patient reported hives.', indicator: 'critical',
      source: { label: 'Allergy check', url: 'https://src.example.org' },
    });

    const card = el.querySelector('[data-testid="cds-card"]')!;
    expect(card.classList).toContain('card--critical');
    expect(el.querySelector('.card__indicator')?.textContent).toContain('Critical');
    expect(el.querySelector('.card__summary')?.textContent).toContain('Allergy to penicillin');
    expect(el.querySelector('.card__detail')?.textContent).toContain('Patient reported hives.');
    expect(el.querySelector('.card__source a')?.getAttribute('href')).toBe('https://src.example.org');
  });

  it('shows markdown detail as plain text, never as markup', () => {
    const el = render({ summary: 's', detail: '<img src=x onerror=alert(1)> **bold**', indicator: 'info', source: { label: 'x' } });

    expect(el.querySelector('.card__detail img')).toBeNull();
    expect(el.querySelector('.card__detail')?.textContent).toContain('<img src=x onerror=alert(1)> **bold**');
  });

  it('makes only http(s) links clickable', () => {
    const el = render({
      summary: 's', indicator: 'info', source: { label: 'Src', url: 'javascript:alert(1)' },
      links: [
        { label: 'Safe', url: 'https://ok.example.org', type: 'absolute' },
        { label: 'Unsafe', url: 'javascript:alert(1)', type: 'absolute' },
        { label: 'App', url: 'https://app.example.org/launch', type: 'smart' },
      ],
    });

    const anchors = Array.from(el.querySelectorAll('.card__links a')).map(a => a.textContent?.trim());
    expect(anchors.length).toBe(2);
    expect(anchors[0]).toContain('Safe');
    expect(el.querySelector('.card__links')?.textContent).toContain('Unsafe');
    expect(el.querySelector('.card__link-type')?.textContent).toContain('SMART app');
    expect(el.querySelector('.card__source a')).toBeNull();
  });

  it('falls back to info for an unknown indicator and lists override reasons', () => {
    const el = render({
      summary: 's', indicator: 'bogus' as CdsCard['indicator'], source: { label: 'x' },
      overrideReasons: [{ code: 'r1', display: 'Benefit outweighs risk' }, { code: 'r2' }],
    });

    expect(el.querySelector('.card__indicator')?.textContent).toContain('Info');
    const chips = Array.from(el.querySelectorAll('.card__chip')).map(c => c.textContent?.trim());
    expect(chips).toEqual(['Benefit outweighs risk', 'r2']);
  });
});
