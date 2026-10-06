import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { NewWorkflowDialogComponent } from './new-workflow-dialog.component';
import { WorkflowApiService } from '../../../services/workflow-api.service';
import { ToastService } from '../../../services/toast.service';

/** The "New workflow" dialog, moved out of the Workflows list so the Dashboard's New button can use it too —
 *  these pin the behaviour it had there. */
describe('NewWorkflowDialogComponent', () => {
  let fixture: ComponentFixture<NewWorkflowDialogComponent>;
  let dialog: NewWorkflowDialogComponent;
  let api: jasmine.SpyObj<WorkflowApiService>;
  let router: jasmine.SpyObj<Router>;
  let toast: jasmine.SpyObj<ToastService>;
  let closed: number;
  const el = () => fixture.nativeElement as HTMLElement;
  const createButton = () => el().querySelector<HTMLButtonElement>('.btn-cta')!;

  beforeEach(() => {
    api = jasmine.createSpyObj<WorkflowApiService>('WorkflowApiService', ['save']);
    router = jasmine.createSpyObj<Router>('Router', ['navigate']);
    toast = jasmine.createSpyObj<ToastService>('ToastService', ['error']);
    TestBed.configureTestingModule({
      imports: [NewWorkflowDialogComponent],
      providers: [
        { provide: WorkflowApiService, useValue: api },
        { provide: Router, useValue: router },
        { provide: ToastService, useValue: toast },
      ],
    });
    fixture = TestBed.createComponent(NewWorkflowDialogComponent);
    dialog = fixture.componentInstance;
    closed = 0;
    dialog.closed.subscribe(() => closed++);
    fixture.detectChanges();
  });

  it('shows the New workflow form, with Create disabled until a name is entered', () => {
    expect(el().querySelector('#new-workflow-title')?.textContent).toBe('New workflow');
    expect(createButton().disabled).toBeTrue();
    dialog.name.set('Epic Prod → SQL');
    fixture.detectChanges();
    expect(createButton().disabled).toBeFalse();
  });

  it('closes straight away when nothing was typed', () => {
    dialog.cancel();
    expect(closed).toBe(1);
    expect(dialog.confirmDiscard()).toBeFalse();
  });

  it('asks before discarding typed input: Keep editing keeps it, Discard closes', () => {
    dialog.description.set('Moves patients');
    dialog.cancel();
    fixture.detectChanges();
    expect(closed).toBe(0);
    expect(el().querySelector('#discard-new-workflow-title')?.textContent).toBe('Discard this workflow?');

    dialog.keepEditing();
    fixture.detectChanges();
    expect(el().querySelector('#discard-new-workflow-title')).toBeNull();
    expect(dialog.description()).toBe('Moves patients');

    dialog.cancel();
    dialog.discard();
    expect(closed).toBe(1);
  });

  it('Escape asks the same way as Cancel', () => {
    dialog.name.set('X');
    el().querySelector('.modal-overlay')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(dialog.confirmDiscard()).toBeTrue();
  });

  it('creates an empty, enabled workflow, closes, and opens the builder on it', () => {
    api.save.and.returnValue(of({ id: 'wf-42' }) as never);
    dialog.name.set('  Epic Prod → SQL  ');
    dialog.create();
    expect(api.save).toHaveBeenCalledOnceWith({ name: 'Epic Prod → SQL', description: null, isEnabled: true, nodes: [], edges: [] });
    expect(closed).toBe(1);
    expect(router.navigate).toHaveBeenCalledOnceWith(['/workflow-builder-v2'], { queryParams: { id: 'wf-42', new: '1' } });
  });

  it('on a failed create, says why and stays open', () => {
    api.save.and.returnValue(throwError(() => ({ error: { title: 'Name already in use' } })) as never);
    dialog.name.set('Dup');
    dialog.create();
    expect(toast.error).toHaveBeenCalledWith('Name already in use');
    expect(closed).toBe(0);
    expect(dialog.creating()).toBeFalse();
    expect(router.navigate).not.toHaveBeenCalled();
  });
});
