import { Component, Type, input } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { FieldMappingCreateTableModalComponent as ModalV2 } from './field-mapping-create-table-modal.component';
import { FieldMappingCreateTableModalComponent as ModalV1 } from '../../../node-library/destination-wizard/field-mapping/field-mapping-create-table-modal.component';

/**
 * "Create a new destination table", opened from the Map fields canvas, choosing "Yes — child of another table":
 * - the dialog grew past the canvas area (its limit was the window height), cutting off its title and buttons;
 * - the Parent table list opened right by the sidebar and down by the header, because the canvas sits inside a
 *   blurred overlay and a backdrop-filter ancestor is what position: fixed then measures from;
 * - the Columns list scrolled sideways — the PK/FK badge was wider than its 28px column, and a long FK name
 *   widened the row.
 * The host reproduces that page: a content area offset by a 240px sidebar and 60px header, with backdrop-filter.
 * Karma loads the real stylesheets, so every size below is what the browser lays out.
 */
function hostFor(modal: Type<unknown>) {
  @Component({
    standalone: true,
    imports: [modal],
    template: `
      <div class="content" [style.height.px]="height()"
           style="position: fixed; left: 240px; top: 60px; width: 1200px; backdrop-filter: blur(1px);">
        <div style="position: relative; width: 100%; height: 100%;">
          <app-field-mapping-create-table-modal [existingTables]="tables" [columnsForTable]="cols" />
        </div>
      </div>`,
  })
  class HostComponent {
    readonly height = input(600);
    tables = ['dbo._Patient_NewMapped_16Sep2026', 'dbo.AccountContextLinks'];
    cols = () => ['PatientId', 'Identifier'];
  }
  return HostComponent;
}

function suite(label: string, modalType: Type<unknown>): void {
  describe(label, () => {
    async function mount(height: number) {
      const host = hostFor(modalType);
      TestBed.configureTestingModule({ imports: [host] });
      const fixture = TestBed.createComponent(host);
      fixture.componentRef.setInput('height', height);
      fixture.detectChanges();
      await fixture.whenStable();
      const el = fixture.nativeElement as HTMLElement;
      const modal = fixture.debugElement.query(By.directive(modalType)).componentInstance as {
        onRelationChange(v: 'standalone' | 'child'): void;
        onFkColumnNameInput(v: string): void;
      };
      const settle = async () => { fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges(); };
      return { fixture, el, modal, settle };
    }

    afterEach(() => document.querySelectorAll('.fm-createtable-parent-panel').forEach(p => p.remove()));

    it('keeps the title and the buttons inside a short canvas area; only the middle scrolls', async () => {
      const v = await mount(330);
      v.modal.onRelationChange('child');
      await v.settle();
      const area = v.el.querySelector('.content')!.getBoundingClientRect();
      const title = v.el.querySelector('.fm-createtable-title')!.getBoundingClientRect();
      const actions = v.el.querySelector('.fm-createtable-actions')!.getBoundingClientRect();
      const body = v.el.querySelector<HTMLElement>('.fm-createtable-body')!;

      expect(title.top).toBeGreaterThanOrEqual(area.top);
      expect(actions.bottom).toBeLessThanOrEqual(area.bottom);
      expect(body.scrollHeight).withContext('the fields scroll instead').toBeGreaterThan(body.clientHeight);
    });

    it('shows columns that were just added, even in a short canvas area', async () => {
      const v = await mount(560);
      const modal = v.modal as unknown as { addColumn(): void };
      v.modal.onRelationChange('child');
      modal.addColumn();
      modal.addColumn();
      await v.settle();
      const columns = v.el.querySelector<HTMLElement>('.fm-createtable-columns')!;
      expect(columns.querySelectorAll('.fm-createtable-col-row').length).toBe(4);   // Id, FK, and the two added
      expect(columns.clientHeight).withContext('the list collapsed to 0px').toBeGreaterThan(0);
      const lastRow = columns.querySelectorAll('.fm-createtable-col-row')[3].getBoundingClientRect();
      expect(lastRow.height).toBeGreaterThan(0);
    });

    it('opens the Parent table list right under its field, even inside a blurred, offset content area', async () => {
      const v = await mount(600);
      v.modal.onRelationChange('child');
      await v.settle();
      const trigger = v.el.querySelector<HTMLButtonElement>('.fm-createtable-parent-trigger')!;
      trigger.click();
      await v.settle();
      await new Promise(resolve => setTimeout(resolve));
      await v.settle();

      const t = trigger.getBoundingClientRect();
      const panel = document.querySelector<HTMLElement>('.fm-createtable-parent-panel')!;
      const p = panel.getBoundingClientRect();
      expect(getComputedStyle(panel).visibility).toBe('visible');
      expect(Math.abs(p.left - t.left)).withContext('was 240px — the sidebar').toBeLessThanOrEqual(1);
      // Opens below the field, or above it when there isn't room below — either way 6px from it.
      const gapBelow = Math.abs(p.top - (t.bottom + 6));
      const gapAbove = Math.abs(t.top - 6 - p.bottom);
      expect(Math.min(gapBelow, gapAbove)).withContext('was 58px — the header').toBeLessThanOrEqual(1);
    });

    it('puts the cursor in the search box of the list once the list is visible', async () => {
      const v = await mount(600);
      v.modal.onRelationChange('child');
      await v.settle();
      v.el.querySelector<HTMLButtonElement>('.fm-createtable-parent-trigger')!.click();
      await v.settle();
      await new Promise(resolve => setTimeout(resolve));
      expect(document.activeElement?.classList.contains('fm-createtable-parent-search'))
        .withContext('focus stayed in Table name while the list was still hidden')
        .toBeTrue();
    });

    it('waits for the list to render before aligning, and marks it aligned only once corrected', async () => {
      const v = await mount(600);
      v.modal.onRelationChange('child');
      await v.settle();
      v.el.querySelector<HTMLButtonElement>('.fm-createtable-parent-trigger')!.click();
      const style = () => (v.modal as unknown as { parentTableMenuStyle(): { aligned?: boolean } | null }).parentTableMenuStyle();
      expect(style()?.aligned).withContext('not before it has been measured').toBeFalsy();
      await v.settle();
      await new Promise(resolve => setTimeout(resolve, 50));
      await v.settle();
      const t = v.el.querySelector('.fm-createtable-parent-trigger')!.getBoundingClientRect();
      const p = document.querySelector<HTMLElement>('.fm-createtable-parent-panel')!.getBoundingClientRect();
      expect(style()?.aligned).toBeTrue();
      expect(Math.abs(p.left - t.left)).toBeLessThanOrEqual(1);
    });

    it('closes the list when the dialog body scrolls, so it is never left stranded away from its field', async () => {
      const v = await mount(330);
      v.modal.onRelationChange('child');
      await v.settle();
      v.el.querySelector<HTMLButtonElement>('.fm-createtable-parent-trigger')!.click();
      await v.settle();
      expect(document.querySelector('.fm-createtable-parent-panel')).not.toBeNull();
      v.el.querySelector<HTMLElement>('.fm-createtable-body')!.dispatchEvent(new Event('scroll'));
      await v.settle();
      expect(document.querySelector('.fm-createtable-parent-panel')).toBeNull();
    });

    it('has no sideways scrolling in Columns, with or without the FK row', async () => {
      const v = await mount(600);
      const columns = () => v.el.querySelector<HTMLElement>('.fm-createtable-columns')!;
      expect(columns().scrollWidth).toBeLessThanOrEqual(columns().clientWidth);

      v.modal.onRelationChange('child');
      v.modal.onFkColumnNameInput('_Patient_NewMapped_16Sep2026_A_Much_Longer_Foreign_Key_ColumnId');
      await v.settle();
      expect(columns().scrollWidth).toBeLessThanOrEqual(columns().clientWidth);
    });

    it('truncates a long FK column name with "…" and shows it in full on hover', async () => {
      const v = await mount(600);
      const longName = '_Patient_NewMapped_16Sep2026_A_Much_Longer_Foreign_Key_ColumnId';
      v.modal.onRelationChange('child');
      v.modal.onFkColumnNameInput(longName);
      await v.settle();
      const name = Array.from(v.el.querySelectorAll<HTMLElement>('.fm-createtable-col-name')).find(n => n.textContent?.includes('Foreign'))!;
      expect(getComputedStyle(name).textOverflow).toBe('ellipsis');
      expect(name.scrollWidth).withContext('the text is longer than the box').toBeGreaterThan(name.clientWidth);
      expect(name.title).toBe(longName);
    });
  });
}

describe('Create table modal — layout', () => {
  suite('v2 (workflow-builder-v2)', ModalV2);
  suite('v1 (Transformation Rules page)', ModalV1);
});
