import { TestBed } from '@angular/core/testing';
import { CdsCardLinksComponent } from './cds-card-links.component';
import { CdsLink } from '../../models/cds.models';

describe('CdsCardLinksComponent', () => {
  function render(links: CdsLink[]): HTMLElement {
    TestBed.configureTestingModule({ imports: [CdsCardLinksComponent] });
    const fixture = TestBed.createComponent(CdsCardLinksComponent);
    fixture.componentRef.setInput('links', links);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('makes http(s) links buttons that open in a new tab, and others plain text', () => {
    const el = render([
      { label: 'Guide', url: 'https://guide.example.org', type: 'absolute' },
      { label: 'Bad', url: 'javascript:alert(1)', type: 'absolute' },
    ]);

    const anchors = el.querySelectorAll('[data-testid="cds-card-link"]');
    expect(anchors.length).toBe(1);
    expect(anchors[0].getAttribute('href')).toBe('https://guide.example.org');
    expect(anchors[0].getAttribute('target')).toBe('_blank');
    expect(anchors[0].getAttribute('rel')).toContain('noopener');
    expect(el.querySelector('.link--plain')?.textContent).toContain('Bad');
  });

  it('marks SMART links, and says when they may open on their own', () => {
    const el = render([
      { label: 'App', url: 'https://app.example.org/launch', type: 'smart' },
      { label: 'Auto', url: 'https://app.example.org/launch', type: 'smart', autolaunchable: true },
      { label: 'Web', url: 'https://w.example.org', type: 'absolute' },
    ]);

    const notes = Array.from(el.querySelectorAll('.card__link-type')).map(n => n.textContent?.trim());
    expect(notes.length).toBe(2);
    expect(notes[0]).toBe('SMART app, opens inside the EHR');
    expect(notes[1]).toContain('and may open on its own');
  });
});
