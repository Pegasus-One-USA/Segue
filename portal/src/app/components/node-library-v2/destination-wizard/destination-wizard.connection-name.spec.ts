import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { FormControl, Validators } from '@angular/forms';
import { of } from 'rxjs';
import { DestinationWizardComponent } from './destination-wizard.component';
import { CanvasNode } from '../../../models/node-v2.model';
import { DestinationConfigurationService } from '../../../destination-connections/services/destination-configuration.service';
import { DestinationConfigurationDto } from '../../../models/destination-configuration-v2.model';
import { WizardDestinationFormApi } from './destination-forms/destination-form-api';
import { ISourceConnectionService } from '../../../source-connections/services/i-source-connection.service';

/** A stand-in for any destination's Step 1 form (here a CSV one), so the wizard's own name handling is tested alone. */
function fakeForm(name: string) {
  const nameControl = new FormControl<string | null>(name, [Validators.required]);
  const config = () => ({ dest_name: nameControl.value ?? '', dest_filePattern: 'out.csv', dest_deliveryMode: 'blob' });
  return {
    nameControl: () => nameControl,
    isValid: () => nameControl.valid,
    getRawValue: () => ({ name: nameControl.value }),
    getFullConfig: config,
    getMetadata: () => (nameControl.valid ? { fields: config(), secret: '' } : null),
    patchFrom: () => undefined,
    reset: () => undefined,
  };
}

/**
 * Every destination's Step 1 is only the Connection (saved ones plus New connection): the type's own name field is
 * hidden there, the name follows the connection, and Review is where it is changed.
 */
describe('DestinationWizardComponent — the destination name', () => {
  let fixture: ComponentFixture<DestinationWizardComponent>;
  let wizard: DestinationWizardComponent;
  let destinations: jasmine.SpyObj<DestinationConfigurationService>;

  const create = (destType = 'csv') => {
    fixture = TestBed.createComponent(DestinationWizardComponent);
    wizard = fixture.componentInstance;
    fixture.componentRef.setInput('destType', destType);
    fixture.componentRef.setInput('attachNode', { id: 'src', x: 0, y: 0, connected: true, fields: {} } as CanvasNode);
  };
  const useForm = (form: ReturnType<typeof fakeForm>) =>
    spyOn(wizard, 'activeForm').and.returnValue(form as unknown as WizardDestinationFormApi);
  /** Review's Add to Workflow, stopped before the workflow save itself. */
  const finish = () => {
    spyOn(wizard, 'flushPendingSchemaOps').and.returnValue(of(false));
    wizard.step.set(wizard.TOTAL_STEPS);
    wizard.next();
  };

  beforeEach(() => {
    destinations = jasmine.createSpyObj<DestinationConfigurationService>('DestinationConfigurationService',
      ['getPaged', 'hasExecutionHistory', 'create', 'update']);
    destinations.getPaged.and.returnValue(of({ items: [], totalCount: 0, page: 1, pageSize: 100 }) as never);
    destinations.hasExecutionHistory.and.returnValue(of({ hasExecutionHistory: false }) as never);
    destinations.create.and.returnValue(of({ id: 'new', keyVaultName: 'kv', secretName: 's' }) as never);
    destinations.update.and.callFake((id: string) => of({ id, keyVaultName: 'kv', secretName: 's' }) as never);

    TestBed.configureTestingModule({
      imports: [DestinationWizardComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: DestinationConfigurationService, useValue: destinations },
        { provide: ISourceConnectionService, useValue: { getAll: () => of([]), getById: () => of({}) } },
      ],
    });
  });

  describe('Step 1', () => {
    it('hides the type\'s own name field, and does not require it', async () => {
      create();
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#cf-name')!;
      expect(input).not.toBeNull();
      expect(getComputedStyle(input.closest('.dw-field')!).display).toBe('none');
      expect(wizard.activeForm()!.nameControl().hasValidator(Validators.required)).toBeFalse();
    });

    it('a new connection with no name takes the type\'s label on Next, and is created under it', () => {
      create();
      useForm(fakeForm(''));

      wizard.next();

      expect(destinations.create.calls.mostRecent().args[0].name).toBe('CSV');
      expect(wizard.step1Name()).toBe('CSV');
      expect(wizard.step()).toBe(2);
    });
  });

  describe('Review', () => {
    it('shows the name, with the connection\'s name as the placeholder', () => {
      create();
      expect(wizard.reviewName()).toBeNull();
      const form = fakeForm('CSV Export');
      useForm(form);
      wizard.next();

      const name = wizard.reviewName()!;
      expect(name.control).toBe(form.nameControl());
      expect(name.placeholder).toBe('CSV Export');
    });

    it('a name changed on Review renames the connection created on Step 1', () => {
      create();
      const form = fakeForm('CSV Export');
      useForm(form);
      wizard.next();
      form.nameControl().setValue('Nightly CSV');

      finish();

      expect(destinations.update).toHaveBeenCalledTimes(1);
      expect(destinations.update.calls.mostRecent().args[0]).toBe('new');
      expect(destinations.update.calls.mostRecent().args[1].name).toBe('Nightly CSV');
    });

    it('an unchanged name, or one emptied on Review, leaves the connection alone', () => {
      create();
      const form = fakeForm('CSV Export');
      useForm(form);
      wizard.next();
      form.nameControl().setValue('');

      finish();

      expect(form.nameControl().value).toBe('CSV Export');
      expect(destinations.update).not.toHaveBeenCalled();
    });

    it('a picked saved connection is never renamed in place (a changed name forks a new one at save)', () => {
      create();
      const form = fakeForm('Shared CSV');
      useForm(form);
      wizard.existingOptions.set([{ id: 'saved-1', name: 'Shared CSV', destinationType: 'Csv' } as DestinationConfigurationDto]);
      wizard.connectionMode.set('existing');
      wizard.selectedExistingId.set('saved-1');
      wizard.next();
      form.nameControl().setValue('Renamed');

      finish();

      expect(destinations.create).not.toHaveBeenCalled();
      expect(destinations.update).not.toHaveBeenCalled();
    });
  });
});
