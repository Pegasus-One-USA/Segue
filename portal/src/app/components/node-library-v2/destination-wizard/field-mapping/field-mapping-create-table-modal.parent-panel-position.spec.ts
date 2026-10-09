import { Component, Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FieldMappingCreateTableModalComponent as ModalV2 } from './field-mapping-create-table-modal.component';
import { FieldMappingCreateTableModalComponent as ModalV1 } from '../../../node-library/destination-wizard/field-mapping/field-mapping-create-table-modal.component';

/**
 * The "Parent table" dropdown is `position: fixed`, and toggleParentTableMenu() gives it coordinates from the
 * trigger's getBoundingClientRect() — i.e. viewport coordinates. That is only correct while "fixed" really
 * means "relative to the viewport".
 *
 * The modal backdrop carries `backdrop-filter: blur(2px)`, and a non-none filter/backdrop-filter makes an
 * element a containing block for its fixed-position descendants. The panel is a DOM descendant of the
 * backdrop, so its top/left resolve against the BACKDROP's corner instead — and that backdrop is
 * `position: absolute; inset: 0` scoped to the field-mapping canvas, not the window. The panel therefore
 * lands right by the sidebar's width and down by the header's height.
 *
 * The host below reproduces exactly that geometry: a positioned ancestor pushed away from the viewport
 * origin, which is what the real canvas is. Both component copies are covered — v1 still backs the live
 * Transformation Rules page, and the two files' positioning code is identical.
 */
@Component({
  standalone: true,
  imports: [ModalV2],
  // The offsets stand in for the sidebar (left) and the header + toolbar (top). Any non-zero pair
  // reproduces the bug; these are close to the real layout so a failure message reads recognisably.
  template: `
    <div class="offset-canvas">
      <app-field-mapping-create-table-modal [existingTables]="tables" />
    </div>
  `,
  styles: [`
    .offset-canvas {
      position: relative;
      margin-left: 260px;
      margin-top: 180px;
      width: 900px;
      height: 600px;
    }
  `],
})
class OffsetCanvasHostV2 {
  readonly tables = ['dbo.Patient', 'dbo.PatientContact'];
}

@Component({
  standalone: true,
  imports: [ModalV1],
  template: `
    <div class="offset-canvas">
      <app-field-mapping-create-table-modal [existingTables]="tables" />
    </div>
  `,
  styles: [`
    .offset-canvas {
      position: relative;
      margin-left: 260px;
      margin-top: 180px;
      width: 900px;
      height: 600px;
    }
  `],
})
class OffsetCanvasHostV1 {
  readonly tables = ['dbo.Patient', 'dbo.PatientContact'];
}

function openParentDropdown(fixture: ComponentFixture<unknown>): { trigger: DOMRect; panel: DOMRect } {
  const root: HTMLElement = fixture.nativeElement;

  // "Yes — child of another table" is what reveals the Parent table field at all.
  const childRadio = Array.from(root.querySelectorAll<HTMLInputElement>('input[type="radio"]'))
    .find(input => input.value === 'child' || input.id?.includes('child'));
  expect(childRadio).withContext('the "child of another table" radio must exist').toBeTruthy();
  childRadio!.click();
  fixture.detectChanges();

  const trigger = root.querySelector<HTMLElement>('.fm-createtable-parent-trigger');
  expect(trigger).withContext('the Parent table trigger must be visible once "child" is chosen').toBeTruthy();
  trigger!.click();
  fixture.detectChanges();

  const panel = root.querySelector<HTMLElement>('.fm-createtable-parent-panel');
  expect(panel).withContext('the dropdown panel must open').toBeTruthy();

  return { trigger: trigger!.getBoundingClientRect(), panel: panel!.getBoundingClientRect() };
}

function itPositionsTheDropdownAgainstTheViewport(label: string, host: Type<unknown>): void {
  describe(label, () => {
    let fixture: ComponentFixture<unknown>;

    beforeEach(async () => {
      await TestBed.configureTestingModule({ imports: [host] }).compileComponents();
      fixture = TestBed.createComponent(host);
      fixture.detectChanges();
    });

    it('opens the parent-table dropdown aligned to its trigger, not offset by the backdrop', () => {
      const { trigger, panel } = openParentDropdown(fixture);

      // One pixel of tolerance for sub-pixel layout; the bug is a ~260px shift, so this cannot pass by luck.
      expect(Math.abs(panel.left - trigger.left))
        .withContext(
          `dropdown left ${panel.left} should match trigger left ${trigger.left}. A large gap means the `
          + 'panel is being positioned against the blurred backdrop instead of the viewport.')
        .toBeLessThanOrEqual(1);
    });

    it('anchors the dropdown to its trigger vertically, on whichever side it opens', () => {
      const { trigger, panel } = openParentDropdown(fixture);

      // toggleParentTableMenu opens downward when there is room (top = trigger.bottom + 6) and flips
      // upward when there is not (bottom = trigger.top - 6). Which branch runs depends on the viewport,
      // and the Karma frame is short enough to flip — so assert adjacency on EITHER side rather than
      // baking in one branch and calling correct behaviour a failure.
      const gapBelow = Math.abs(panel.top - (trigger.bottom + 6));
      const gapAbove = Math.abs(panel.bottom - (trigger.top - 6));

      expect(Math.min(gapBelow, gapAbove))
        .withContext(
          `panel [${panel.top}, ${panel.bottom}] should sit 6px below or above trigger `
          + `[${trigger.top}, ${trigger.bottom}]; a large gap on both sides means it is positioned `
          + 'against the blurred backdrop instead of the viewport.')
        .toBeLessThanOrEqual(1);
    });
  });
}

describe('Create-table modal — parent-table dropdown position', () => {
  itPositionsTheDropdownAgainstTheViewport('v2 (workflow-builder-v2)', OffsetCanvasHostV2);
  itPositionsTheDropdownAgainstTheViewport('v1 (Transformation Rules page)', OffsetCanvasHostV1);
});
