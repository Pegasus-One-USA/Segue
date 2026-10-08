import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MpiIdentifiersDialogComponent } from './mpi-identifiers-dialog.component';
import { MpiNode } from '../../../models/node-v2.model';
import { MPI_IDENTIFIERS_FIELD } from '../../../services/mpi-node.util';

describe('MpiIdentifiersDialogComponent', () => {
  let fixture: ComponentFixture<MpiIdentifiersDialogComponent>;
  let host: HTMLElement;

  const node = (identifiers: string): MpiNode =>
    ({ id: 'mpi', kind: 'mpi', x: 0, y: 0, fields: { [MPI_IDENTIFIERS_FIELD]: identifiers } });

  const checkboxes = () => Array.from(host.querySelectorAll<HTMLInputElement>('.mpi-option__input'));
  const button = (text: string) =>
    Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find(b => b.textContent?.trim() === text);

  function open(identifiers: string, readOnly = false): void {
    fixture.componentRef.setInput('node', node(identifiers));
    fixture.componentRef.setInput('readOnly', readOnly);
    fixture.componentRef.setInput('sourceNames', ['Epic Patient Backend', 'Athena Backend']);
    fixture.componentRef.setInput('open', true);
    fixture.detectChanges();
  }

  function click(index: number): void {
    checkboxes()[index].click();
    fixture.detectChanges();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [MpiIdentifiersDialogComponent] });
    fixture = TestBed.createComponent(MpiIdentifiersDialogComponent);
    host = fixture.nativeElement as HTMLElement;
  });

  it('lists all 18 HIPAA identifiers and the sources being matched', () => {
    open('');
    expect(checkboxes().length).toBe(18);
    expect(host.textContent).toContain('Epic Patient Backend');
    expect(host.textContent).toContain('Athena Backend');
  });

  it('starts from the node’s saved selection', () => {
    open('names,ssn');
    const checked = checkboxes().map(box => box.checked);
    expect(checked.filter(Boolean).length).toBe(2);
    expect(checked[0]).toBeTrue();   // Names
    expect(checked[6]).toBeTrue();   // SSN
    expect(host.querySelector('.mpi-dialog__count')?.textContent).toContain('2');
  });

  it('cannot save an empty selection, and says why', () => {
    open('');
    expect(button('Save identifiers')!.disabled).toBeTrue();
    expect(host.textContent).toContain('Select at least one identifier.');
  });

  it('emits the selection in catalog order, whatever order it was ticked in', () => {
    open('');
    const emitted: string[] = [];
    fixture.componentInstance.saved.subscribe(value => emitted.push(value));

    click(7);  // Medical record numbers
    click(0);  // Names
    button('Save identifiers')!.click();

    expect(emitted).toEqual(['names,mrn']);
  });

  it('selects all and clears', () => {
    open('names');
    button('Select all')!.click();
    fixture.detectChanges();
    expect(checkboxes().every(box => box.checked)).toBeTrue();

    button('Clear')!.click();
    fixture.detectChanges();
    expect(checkboxes().some(box => box.checked)).toBeFalse();
  });

  it('is view-only for a role that cannot edit the workflow', () => {
    open('names', true);
    expect(host.querySelector<HTMLFieldSetElement>('.mpi-dialog__list')!.disabled).toBeTrue();
    expect(button('Save identifiers')).toBeUndefined();
    expect(button('Select all')).toBeUndefined();
    expect(button('Close')).toBeDefined();
  });

  it('closes on Cancel without saving', () => {
    open('names');
    let closed = 0;
    let saved = 0;
    fixture.componentInstance.closed.subscribe(() => closed++);
    fixture.componentInstance.saved.subscribe(() => saved++);

    click(1);
    button('Cancel')!.click();

    expect(closed).toBe(1);
    expect(saved).toBe(0);
  });
});
