import { TestBed } from '@angular/core/testing';
import { CdsLinksEditorComponent } from './cds-links-editor.component';
import { CDS_LINK_MAX, CdsLinksArray, buildCdsLinksArray, readCdsLinks } from '../../models/cds-service-options.util';

describe('CdsLinksEditorComponent', () => {
  function render(links: CdsLinksArray = buildCdsLinksArray(null)) {
    TestBed.configureTestingModule({ imports: [CdsLinksEditorComponent] });
    const fixture = TestBed.createComponent(CdsLinksEditorComponent);
    fixture.componentRef.setInput('links', links);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const add = () => {
      (el.querySelector('[data-testid="cds-link-add"]') as HTMLButtonElement).click();
      fixture.detectChanges();
    };
    const type = (sel: string, value: string) => {
      const input = el.querySelector(sel) as HTMLInputElement;
      input.value = value;
      input.dispatchEvent(new Event('input'));
      input.dispatchEvent(new Event('blur'));
      fixture.detectChanges();
    };
    const pick = (sel: string, value: string) => {
      const select = el.querySelector(sel) as HTMLSelectElement;
      select.value = value;
      select.dispatchEvent(new Event('change'));
      fixture.detectChanges();
    };
    return { fixture, editor: fixture.componentInstance, el, links, add, type, pick };
  }

  it('starts with no links', () => {
    const { el } = render();
    expect(el.textContent).toContain('No links.');
    expect(el.querySelectorAll('[data-testid="cds-link-row"]').length).toBe(0);
  });

  it('adds a web link, with no SMART-only fields', () => {
    const { el, add, links } = render();
    add();

    expect(links.length).toBe(1);
    expect(links.dirty).toBeTrue();
    expect(el.querySelector('#cds-link-context-0')).toBeNull();
    expect(el.querySelector('#cds-link-auto-0')).toBeNull();
    expect(el.textContent).toContain('Web address');
  });

  it('shows app context, auto-launch and the allow-list note for a SMART link only', () => {
    const { el, add, pick, type, links } = render();
    add();
    type('#cds-link-label-0', 'Dose calculator');
    type('#cds-link-url-0', 'https://app.example.org/launch');
    pick('#cds-link-type-0', 'smart');

    expect(el.querySelector('#cds-link-context-0')).not.toBeNull();
    expect(el.querySelector('#cds-link-auto-0')).not.toBeNull();
    expect(el.textContent).toContain('App launch URL');
    expect(el.textContent).toContain('SMART app allow-list');

    type('#cds-link-context-0', 'mode=renal');
    (el.querySelector('#cds-link-auto-0') as HTMLInputElement).click();
    expect(readCdsLinks(links)).toEqual([{
      label: 'Dose calculator', url: 'https://app.example.org/launch', type: 'smart', appContext: 'mode=renal', autolaunchable: true,
    }]);

    // Back to a web page: the SMART-only values are not sent.
    pick('#cds-link-type-0', 'absolute');
    expect(el.querySelector('#cds-link-context-0')).toBeNull();
    expect(readCdsLinks(links)?.[0]).toEqual(jasmine.objectContaining({ appContext: null, autolaunchable: false }));
  });

  it('asks for an https address', () => {
    const { el, add, type, links } = render();
    add();
    type('#cds-link-label-0', 'Guide');
    type('#cds-link-url-0', 'http://guide.example.org');

    expect(links.invalid).toBeTrue();
    expect(el.textContent).toContain('Enter a full web address starting with https://.');

    type('#cds-link-url-0', '');
    expect(el.textContent).toContain('Enter a web address.');

    type('#cds-link-url-0', 'https://guide.example.org');
    expect(links.valid).toBeTrue();
  });

  it('shows untouched errors after a save attempt', () => {
    const { el, add, fixture } = render();
    add();
    expect(el.textContent).not.toContain('Enter the text on the button.');
    fixture.componentRef.setInput('showAllErrors', true);
    fixture.detectChanges();
    expect(el.textContent).toContain('Enter the text on the button.');
  });

  it(`removes a link and stops adding at ${CDS_LINK_MAX}`, () => {
    const { el, add, links, fixture, editor } = render();
    for (let i = 0; i < CDS_LINK_MAX; i++) add();
    expect(el.querySelector('[data-testid="cds-link-add"]')).toBeNull();
    editor.add();
    expect(links.length).toBe(CDS_LINK_MAX);

    (el.querySelector('[aria-label="Remove link 1"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(links.length).toBe(CDS_LINK_MAX - 1);
  });

  it('offers no edit buttons when disabled', () => {
    const links = buildCdsLinksArray(null);
    links.disable();
    const { el } = render(links);
    expect(el.querySelector('[data-testid="cds-link-add"]')).toBeNull();
  });
});
