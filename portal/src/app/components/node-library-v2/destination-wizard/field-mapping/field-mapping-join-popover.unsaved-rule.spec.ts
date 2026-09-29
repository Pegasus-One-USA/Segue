import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { FieldMappingJoinPopoverComponent as PopoverV2 } from './field-mapping-join-popover.component';
import { FieldMappingJoinPopoverComponent as PopoverV1 } from '../../../node-library/destination-wizard/field-mapping/field-mapping-join-popover.component';
import { MappingRow } from './field-mapping-model';

/**
 * Configuring a transformation here only reaches the server when "Add rule"/"Update rule" is pressed —
 * ruleConfig is a plain signal until then. Closing the popover threw that away with no warning and nothing
 * on the server to recover, so a mode + separator typed but not saved was simply gone.
 *
 * Covers both component copies: v1 still backs the live Transformation Rules page.
 */
function row(): MappingRow {
  return {
    resource: 'Patient',
    tableName: 'dbo.Patient',
    targetName: 'Fullname',
    sources: [{ fhirPath: 'Patient.name.given', label: 'given' }],
  } as unknown as MappingRow;
}

function popoverSuite(label: string, component: Type<unknown>): void {
  describe(label, () => {
    let fixture: ComponentFixture<unknown>;
    let popover: {
      ruleSectionOpen: { set(v: boolean): void };
      ruleConfig: { set(v: Record<string, string>): void; (): Record<string, string> };
      hasUnsavedRule(): boolean;
      onClose(): void;
      onSave(): void;
      discardAndContinue(): void;
      toggleRuleSection(): void;
    };
    let closed: number;
    let saved: number;

    beforeEach(async () => {
      await TestBed.configureTestingModule({
        imports: [component],
        providers: [provideHttpClient(), provideHttpClientTesting()],
      }).compileComponents();

      fixture = TestBed.createComponent(component);
      fixture.componentRef.setInput('row', row());
      fixture.componentRef.setInput('rulesDestinationType', 'SqlServer');
      fixture.detectChanges();

      // The popover looks up any existing rule on open; answer "none authored yet".
      const http = TestBed.inject(HttpTestingController);
      http.match(() => true).forEach(r => r.flush([]));
      fixture.detectChanges();

      popover = fixture.componentInstance as typeof popover;
      closed = 0;
      saved = 0;
      const instance = fixture.componentInstance as {
        closed: { subscribe(fn: () => void): void };
        save: { subscribe(fn: () => void): void };
      };
      instance.closed.subscribe(() => closed++);
      instance.save.subscribe(() => saved++);
    });

    it('reports no unsaved rule before anything is configured', () => {
      popover.toggleRuleSection();
      fixture.detectChanges();

      expect(popover.hasUnsavedRule())
        .withContext('opening the section and touching nothing is not an edit')
        .toBeFalse();
    });

    it('reports an unsaved rule once the config is changed', () => {
      popover.toggleRuleSection();
      fixture.detectChanges();

      // What the reporter did: concat mode with "|" as the join separator, never saved.
      popover.ruleConfig.set({ ...popover.ruleConfig(), mode: 'concat', separator: '|' });
      fixture.detectChanges();

      expect(popover.hasUnsavedRule()).toBeTrue();
    });

    it('does not close while an unsaved rule config would be discarded', () => {
      popover.toggleRuleSection();
      fixture.detectChanges();
      popover.ruleConfig.set({ ...popover.ruleConfig(), mode: 'concat', separator: '|' });
      fixture.detectChanges();

      popover.onClose();
      fixture.detectChanges();

      expect(closed)
        .withContext('closing must ask first — the config was never sent to the server, so there is '
          + 'nothing to recover once the popover is gone')
        .toBe(0);
    });

    it('closes immediately when there is nothing unsaved', () => {
      popover.onClose();
      fixture.detectChanges();

      expect(closed).withContext('an untouched popover must not nag').toBe(1);
    });

    it('does not save the mapping while an unsaved rule config would be discarded', () => {
      // Save is the likelier of the two exits: the canvas handler calls closePopover() right after the
      // mapping is written, so an unguarded Save throws the rule away exactly as ✕ did.
      popover.toggleRuleSection();
      fixture.detectChanges();
      popover.ruleConfig.set({ ...popover.ruleConfig(), mode: 'concat', separator: '|' });
      fixture.detectChanges();

      popover.onSave();
      fixture.detectChanges();

      expect(saved).withContext('the mapping save must wait for an answer').toBe(0);
    });

    it('saves the mapping once the author chooses to discard', () => {
      popover.toggleRuleSection();
      fixture.detectChanges();
      popover.ruleConfig.set({ ...popover.ruleConfig(), mode: 'concat', separator: '|' });
      fixture.detectChanges();
      popover.onSave();
      fixture.detectChanges();

      popover.discardAndContinue();
      fixture.detectChanges();

      expect(saved).withContext('discarding from the SAVE route must still save the mapping').toBe(1);
      expect(closed).withContext('and must not close instead of saving').toBe(0);
    });

    it('saves immediately when there is nothing unsaved', () => {
      popover.onSave();
      fixture.detectChanges();

      expect(saved).toBe(1);
    });
  });
}

describe('Join popover — unsaved transformation rule', () => {
  popoverSuite('v2 (workflow-builder-v2)', PopoverV2);
  popoverSuite('v1 (Transformation Rules page)', PopoverV1);
});
