import { TestBed } from '@angular/core/testing';
import { CanvasComponent } from './canvas.component';
import { PipelineStoreV2 } from '../../services/pipeline-v2.store';
import { ToastService } from '../../services/toast.service';
import { PermissionService } from '../../auth/services/permission.service';
import { TransformationRulesService } from '../node-library-v2/destination-wizard/field-mapping/transformation-rules.service';
import { CanvasNode } from '../../models/node-v2.model';

/** A Transformation step the builder added for rules carries __autoAdded, so the builder may remove it again once its
 *  rules are gone. A COPY the user pastes is a step the user placed: it must not inherit that marker. */
describe('Canvas — pasting a builder-added step makes it the user\'s', () => {
  it('drops __autoAdded from the pasted copy and leaves the original untouched', () => {
    TestBed.configureTestingModule({
      providers: [
        { provide: ToastService, useValue: jasmine.createSpyObj('ToastService', ['show', 'error', 'success', 'warning']) },
        { provide: PermissionService, useValue: { hasPermission: () => true, hasAny: () => true } },
        { provide: TransformationRulesService, useValue: {} },
      ],
    });
    TestBed.overrideComponent(CanvasComponent, { set: { template: '', imports: [] } });
    const store = TestBed.inject(PipelineStoreV2);
    store.loadGraph([
      { id: 'tra', kind: 'transform', transformId: 'transformation', x: 0, y: 0,
        fields: { __name: 'Transformation', __autoAdded: 'true' } } as unknown as CanvasNode,
    ], []);
    const canvas = TestBed.createComponent(CanvasComponent).componentInstance as unknown as {
      ctxMenu: { set(menu: object): void }; ctxCopy(): void; ctxPaste(): void;
    };

    canvas.ctxMenu.set({ type: 'node', x: 0, y: 0, flowX: 0, flowY: 0, nodeId: 'tra' });
    canvas.ctxCopy();
    canvas.ctxMenu.set({ type: 'canvas', x: 0, y: 0, flowX: 300, flowY: 0 });
    canvas.ctxPaste();

    const pasted = store.nodes().find(node => node.id !== 'tra')!;
    expect(pasted.fields['__autoAdded']).toBeUndefined();
    expect(pasted.fields['__name']).toBe('Transformation');
    expect(store.byId('tra')!.fields['__autoAdded']).toBe('true');
  });
});
