import { ComponentFixture, TestBed } from '@angular/core/testing';
import { EhrWriteKindListComponent } from './ehr-write-kind-list.component';
import { HOLDER_ENCOUNTER_KIND_NOTE, NO_KIND_CHOSEN_NOTE, kindsFor } from './ehr-write-kinds.model';
import { ECW_CAPABILITIES, EPIC_CAPABILITIES } from './ehr-write-capabilities.fixtures';

describe('EhrWriteKindListComponent', () => {
  let fixture: ComponentFixture<EhrWriteKindListComponent>;
  let toggled: string[];

  const set = (inputs: Record<string, unknown>) => {
    for (const [key, value] of Object.entries(inputs)) fixture.componentRef.setInput(key, value);
    fixture.detectChanges();
  };
  const el = () => fixture.nativeElement as HTMLElement;
  const box = (id: string) => el().querySelector<HTMLInputElement>(`[data-kind="${id}"] input`)!;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [EhrWriteKindListComponent] });
    fixture = TestBed.createComponent(EhrWriteKindListComponent);
    toggled = [];
    fixture.componentInstance.toggled.subscribe((id) => toggled.push(id));
    set({ kinds: kindsFor('DocumentReference', EPIC_CAPABILITIES, false) });
  });

  it('renders one checkbox per kind, in plain words', () => {
    expect(Array.from(el().querySelectorAll('[data-kind] .ewkl-label')).map((k) => k.textContent!.trim()))
      .toEqual(['Clinical notes', 'Scanned documents']);
    expect(el().querySelector('[data-kind="document-information"]')!.textContent).not.toContain('included');
  });

  it('says "always included" only beside a ticked box; until the type is ticked, says when it is sent', () => {
    const hint = () => el().querySelector('[data-kind="clinical-note"] [data-testid="ewkl-included-hint"]')!.textContent!.trim();
    expect(box('clinical-note').checked).toBeFalse();
    expect(hint()).toBe('included whenever DocumentReference is ticked');

    set({ typeSelected: true, chosen: ['clinical-note'] });
    expect(box('clinical-note').checked).toBeTrue();
    expect(hint()).toBe('always included');
  });

  it('locks the always-included kind on while the type is ticked, and emits the optional one', () => {
    set({ typeSelected: true, chosen: ['clinical-note'] });
    expect(box('clinical-note').checked).toBeTrue();
    expect(box('clinical-note').disabled).toBeTrue();
    expect(box('document-information').checked).toBeFalse();

    box('document-information').click();
    expect(toggled).toEqual(['document-information']);
  });

  it('lets any kind of an unticked type be ticked (which ticks the type)', () => {
    expect(box('clinical-note').disabled).toBeFalse();
    box('clinical-note').click();
    expect(toggled).toEqual(['clinical-note']);
  });

  it('a ticked history kind says it is filed on a new telephone encounter', () => {
    set({ kinds: kindsFor('Procedure', ECW_CAPABILITIES, false), typeSelected: true, chosen: [] });
    expect(el().querySelector('[data-testid="ewkl-holder-note"]')).toBeNull();
    expect(el().querySelector('[data-testid="ewkl-none-chosen"]')!.textContent).toContain(NO_KIND_CHOSEN_NOTE);

    set({ chosen: ['surgical-history'] });
    expect(el().querySelector('[data-testid="ewkl-holder-note"]')!.textContent).toContain(HOLDER_ENCOUNTER_KIND_NOTE);
    expect(el().querySelector('[data-testid="ewkl-none-chosen"]')).toBeNull();
  });

  it('names what a shared switch also ticks', () => {
    set({ kinds: kindsFor('Procedure', ECW_CAPABILITIES, false), shared: { 'surgical-history': 'Also ticks Medical history under Condition' } });
    expect(el().querySelector('[data-testid="ewkl-shared-note"]')!.textContent).toContain('Also ticks Medical history under Condition');
  });
});
