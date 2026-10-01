import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { FieldMappingJoinPopoverComponent as PopoverV2 } from './field-mapping-join-popover.component';
import { FieldMappingJoinPopoverComponent as PopoverV1 } from '../../../node-library/destination-wizard/field-mapping/field-mapping-join-popover.component';
import { UnsavedChangesPromptService } from '../../../../core/services/unsaved-changes-prompt.service';
import { UnsavedChangesRegistryService } from '../../../../core/services/unsaved-changes-registry.service';
import { MappingRow } from './field-mapping-model';

/** Edits in the mapping popover (join delimiter, instance selection, ...) live only here until its Save. They
 *  used not to count as unsaved at all — only an unsaved transformation rule did — so leaving the page dropped
 *  them silently. They now count for leaving the PAGE (menu, back, sign out, refresh). Clicks inside the mapping
 *  screen — ✕, another column — deliberately don't raise that prompt. Covers both copies: v1 still backs the
 *  Transformation Rules page. */
function joinRow(): MappingRow {
  return {
    resource: 'Patient', tableName: 'dbo.Patient', targetName: 'FullName', mode: 'value',
    sources: [{ fhirPath: 'Patient.name.given', label: 'given' }, { fhirPath: 'Patient.name.family', label: 'family' }],
    delimiter: ', ', instance: { type: 'first' },
  } as unknown as MappingRow;
}

type Popover = {
  hasUnsavedEdits(): boolean;
  onDelimiterInput(value: string): void;
  onClose(): void;
  onSave(): void;
  draft(): MappingRow | null;
};

function suite(label: string, component: Type<unknown>): void {
  describe(label, () => {
    let fixture: ComponentFixture<unknown>;
    let popover: Popover;
    let prompt: jasmine.SpyObj<UnsavedChangesPromptService>;
    let closed: number;
    let saved: number;

    beforeEach(async () => {
      prompt = jasmine.createSpyObj<UnsavedChangesPromptService>('UnsavedChangesPromptService', ['confirmDiscard']);
      await TestBed.configureTestingModule({
        imports: [component],
        providers: [provideHttpClient(), provideHttpClientTesting(), { provide: UnsavedChangesPromptService, useValue: prompt }],
      }).compileComponents();
      fixture = TestBed.createComponent(component);
      fixture.componentRef.setInput('row', joinRow());
      fixture.componentRef.setInput('rulesDestinationType', 'SqlServer');
      fixture.detectChanges();
      TestBed.inject(HttpTestingController).match(() => true).forEach(r => r.flush([]));
      fixture.detectChanges();
      popover = fixture.componentInstance as Popover;
      closed = 0;
      saved = 0;
      const out = fixture.componentInstance as { closed: { subscribe(f: () => void): void }; save: { subscribe(f: () => void): void } };
      out.closed.subscribe(() => closed++);
      out.save.subscribe(() => saved++);
    });

    it('opening and touching nothing is not an edit: ✕ closes without asking', () => {
      expect(popover.hasUnsavedEdits()).toBeFalse();
      popover.onClose();
      expect(prompt.confirmDiscard).not.toHaveBeenCalled();
      expect(closed).toBe(1);
    });

    it('a join delimiter change counts as unsaved, but ✕ — a click inside — closes without the page prompt', () => {
      popover.onDelimiterInput(' | ');
      fixture.detectChanges();
      expect(popover.hasUnsavedEdits()).toBeTrue();

      popover.onClose();
      expect(prompt.confirmDiscard).not.toHaveBeenCalled();
      expect(closed).toBe(1);
    });

    it('typing the delimiter back to what it was is not an edit', () => {
      popover.onDelimiterInput(' | ');
      popover.onDelimiterInput(', ');
      fixture.detectChanges();
      expect(popover.hasUnsavedEdits()).toBeFalse();
    });

    it('Save is the way to keep the edit, so it does not ask', () => {
      popover.onDelimiterInput(' | ');
      fixture.detectChanges();
      popover.onSave();
      expect(prompt.confirmDiscard).not.toHaveBeenCalled();
      expect(saved).toBe(1);
    });

    it('registers with the app-wide check while open, and leaves it when closed', () => {
      const registry = TestBed.inject(UnsavedChangesRegistryService);
      expect(registry.hasAnyUnsavedChanges()).toBeFalse();
      popover.onDelimiterInput(' | ');
      fixture.detectChanges();
      expect(registry.hasAnyUnsavedChanges()).toBeTrue();
      fixture.destroy();
      expect(registry.hasAnyUnsavedChanges()).toBeFalse();
    });
  });
}

describe('Join popover — unsaved mapping edits', () => {
  suite('v2 (workflow-builder-v2)', PopoverV2);
  suite('v1 (Transformation Rules page)', PopoverV1);
});
