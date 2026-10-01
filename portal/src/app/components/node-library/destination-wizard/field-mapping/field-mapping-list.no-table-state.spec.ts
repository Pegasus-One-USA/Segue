import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { FieldMappingCanvasComponent } from './field-mapping-canvas.component';
import { FieldMappingListComponent } from './field-mapping-list.component';
import { ToastService } from '../../../../services/toast.service';
import { DestinationSchemaService } from '../../../../services/destination-schema.service';
import { TransformationRulesService } from './transformation-rules.service';

/** Before any destination table is added, "+ Add mapping" opened a form whose table list was empty, so it could
 *  never be submitted, and the list's message pointed at it. These mount the real canvas + list, so adding a
 *  table goes through the same path the screen uses (targetByResource/sqlTableOptions → targetCards →
 *  tablesForResourceFn). */
describe('Field mapping list (v1) — no destination table yet', () => {
  async function mount(destType: string, inputs: Record<string, unknown> = {}) {
    await TestBed.configureTestingModule({
      imports: [FieldMappingCanvasComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ToastService, useValue: { show: jasmine.createSpy('show') } },
        { provide: DestinationSchemaService, useValue: {} },
        { provide: TransformationRulesService, useValue: {
            getNodeSchemas: () => of([]), getEffectiveRules: () => of([]), delete: () => of(void 0),
          } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(FieldMappingCanvasComponent);
    const set = (name: string, value: unknown) => fixture.componentRef.setInput(name, value);
    set('resources', ['Patient']);
    set('destType', destType);
    set('mappingRows', []);
    set('targetByResource', {});
    set('availableFields', () => []);
    set('defaultAvailableFields', () => []);
    set('columnsForResourceTarget', () => []);
    set('hasSqlTables', destType !== 'mongo' && destType !== 'csv');
    set('sqlTableOptions', ['public.patient']);
    for (const [k, v] of Object.entries(inputs)) set(k, v);
    fixture.detectChanges();

    const list = fixture.debugElement.query(By.directive(FieldMappingListComponent)).componentInstance as FieldMappingListComponent;
    (list as unknown as { collapsed: { set(v: boolean): void } }).collapsed.set(false);
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    return {
      fixture, list, set,
      message: () => el.querySelector('.fm-list-empty')?.textContent?.trim() ?? '',
      addButton: () => el.querySelector<HTMLButtonElement>('.fm-list-add'),
    };
  }

  it('PostgreSQL with no table: hides "+ Add mapping" and points at the add-table control', async () => {
    const view = await mount('postgresql');
    expect(view.addButton()).toBeNull();
    expect(view.message()).toBe(
      'No table yet — use "+ Add a table from your database…" above to select an existing table or create a new one. Then you can add mappings here.');
  });

  it('shows the button and the usual message as soon as a table is added, with no reload', async () => {
    const view = await mount('postgresql');
    view.set('targetByResource', { Patient: 'public.patient' });
    view.fixture.detectChanges();

    expect(view.addButton()).not.toBeNull();
    expect(view.message()).toBe('No mappings yet — drag a field onto a column, or use "+ Add mapping" below.');
  });

  it('hides the button again and closes an open form when the last table is removed', async () => {
    const view = await mount('postgresql', { targetByResource: { Patient: 'public.patient' } });
    view.addButton()!.click();
    view.fixture.detectChanges();
    expect((view.list as unknown as { draft: () => unknown }).draft()).not.toBeNull();

    view.set('targetByResource', { Patient: '' });
    view.fixture.detectChanges();

    expect((view.list as unknown as { draft: () => unknown }).draft()).toBeNull();
    expect(view.addButton()).toBeNull();
    expect(view.message()).toContain('No table yet');
  });

  it('MongoDB with no collection: says "collection" and names its own control', async () => {
    const view = await mount('mongo');
    expect(view.addButton()).toBeNull();
    expect(view.message()).toBe(
      'No collection yet — use "+ Add a collection…" above to select an existing collection or create a new one. Then you can add mappings here.');
  });

  it('without rights to create tables, only offers selecting an existing one', async () => {
    const view = await mount('postgresql', { schemaAuthoringEnabled: false });
    expect(view.message()).toBe(
      'No table yet — use "+ Add a table from your database…" above to select an existing table. Then you can add mappings here.');
  });

  it('while the table list is still loading, says so instead of naming a control that isn\'t there yet', async () => {
    const view = await mount('postgresql', { hasSqlTables: false, schemaLoadState: 'loading' });
    expect(view.addButton()).toBeNull();
    expect(view.message()).toBe(
      'No table yet — once your database\'s tables have loaded, add one from the panel above. Then you can add mappings here.');
  });
});
